using System.Text;
using System.Text.RegularExpressions;

namespace FolderAssistant.Tools;

/// <summary>
/// A glob over '/'-separated relative paths: <c>*</c> within a segment, <c>?</c> one character,
/// <c>**</c> any number of segments. A pattern with no separator matches the file name at any depth;
/// one with a separator matches the whole relative path. Case follows the platform, as containment does.
/// </summary>
internal sealed class GlobPattern
{
	private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

	private readonly Regex _regex;

	private GlobPattern(Regex regex, String pattern)
	{
		this._regex = regex;
		this.Pattern = pattern;
	}

	/// <summary>The pattern as given, before a backslash was read as a separator.</summary>
	public String Pattern { get; }

	/// <summary>
	/// Parses <paramref name="pattern"/>, or throws <see cref="ArgumentException"/> for an empty one or one
	/// with a <c>..</c> segment — a walk is confined to the root, and a pattern has no business naming its parent.
	/// </summary>
	public static GlobPattern Parse(String pattern)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

		String normalised = pattern.Trim().Replace('\\', '/').TrimStart('/');
		if (normalised.Length == 0)
		{
			throw new ArgumentException("A pattern must name something under the root.", nameof(pattern));
		}

		String[] segments = normalised.Split('/');
		if (Array.Exists(segments, segment => segment == ".."))
		{
			throw new ArgumentException($"A pattern cannot contain a '..' segment: '{pattern}'.", nameof(pattern));
		}

		StringBuilder regex = new("^");
		if (segments.Length == 1 && segments[0] != "**")
		{
			// No separator: the name, at any depth.
			regex.Append("(?:.*/)?");
		}

		Boolean separatorConsumed = false;
		for (Int32 i = 0; i < segments.Length; i++)
		{
			String segment = segments[i];

			if (segment == "**")
			{
				if (segments.Length == 1)
				{
					regex.Append(".*");
				}
				else if (i == 0)
				{
					// Leading: zero or more segments, each with the separator that follows it, so the
					// next segment must not add one of its own.
					regex.Append("(?:.*/)?");
					separatorConsumed = true;
				}
				else
				{
					// Elsewhere: zero or more segments, each with the separator that precedes it.
					regex.Append("(?:/.*)?");
				}

				continue;
			}

			if (i > 0 && !separatorConsumed)
			{
				regex.Append('/');
			}

			separatorConsumed = false;
			AppendSegment(regex, segment);
		}

		regex.Append('$');

		RegexOptions options = RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;
		if (OperatingSystem.IsWindows())
		{
			options |= RegexOptions.IgnoreCase;
		}

		return new GlobPattern(new Regex(regex.ToString(), options, MatchTimeout), pattern);
	}

	/// <summary>Whether a '/'-separated relative path matches.</summary>
	public Boolean IsMatch(String relativePath)
		=> this._regex.IsMatch(relativePath.Replace('\\', '/'));

	private static void AppendSegment(StringBuilder regex, String segment)
	{
		foreach (Char c in segment)
		{
			switch (c)
			{
				case '*':
					regex.Append("[^/]*");
					break;
				case '?':
					regex.Append("[^/]");
					break;
				default:
					regex.Append(Regex.Escape(c.ToString()));
					break;
			}
		}
	}
}
