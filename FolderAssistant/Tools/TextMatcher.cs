using System.Text.RegularExpressions;

namespace FolderAssistant.Tools;

/// <summary>
/// Finds a pattern in one line of text: a literal substring or a regular expression, with or without
/// case, and optionally only as a whole word. Whole-word is decided by <see cref="WordBoundary"/> on
/// each candidate after it is found, the same way for both forms, so the two cannot disagree about
/// what a word is; a candidate that fails it is passed over and the search goes on along the line.
/// </summary>
internal sealed class TextMatcher
{
	private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

	private readonly String _literal;
	private readonly StringComparison _comparison;
	private readonly Regex? _regex;
	private readonly Boolean _wholeWord;

	private TextMatcher(String literal, StringComparison comparison, Regex? regex, Boolean wholeWord)
	{
		this._literal = literal;
		this._comparison = comparison;
		this._regex = regex;
		this._wholeWord = wholeWord;
	}

	/// <summary>The pattern as given.</summary>
	public String Pattern => this._literal;

	/// <summary>
	/// Builds a matcher, or throws <see cref="ArgumentException"/> for an empty pattern or a regular
	/// expression that does not parse. A pattern of whitespace alone is a search like any other.
	/// </summary>
	public static TextMatcher Create(String pattern, Boolean regex, Boolean ignoreCase, Boolean wholeWord)
	{
		ArgumentException.ThrowIfNullOrEmpty(pattern);

		Regex? compiled = null;
		if (regex)
		{
			RegexOptions options = RegexOptions.CultureInvariant;
			if (ignoreCase)
			{
				options |= RegexOptions.IgnoreCase;
			}

			compiled = new Regex(pattern, options, MatchTimeout);
		}

		return new TextMatcher(
			pattern,
			ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal,
			compiled,
			wholeWord);
	}

	/// <summary>The index of the first match in <paramref name="line"/>, or -1 when there is none.</summary>
	public Int32 IndexIn(String line)
		=> this._regex is null ? this.LiteralIndexIn(line) : this.RegexIndexIn(line);

	private Int32 LiteralIndexIn(String line)
	{
		Int32 index = line.IndexOf(this._literal, 0, this._comparison);
		while (index >= 0)
		{
			if (!this._wholeWord || WordBoundary.IsWholeWord(line, index, this._literal.Length))
			{
				return index;
			}

			// The next candidate may overlap this one ("aaa" holds "aa" twice), so the search resumes one
			// character on, not after the rejected match.
			index = line.IndexOf(this._literal, index + 1, this._comparison);
		}

		return -1;
	}

	private Int32 RegexIndexIn(String line)
	{
		Match match = this._regex!.Match(line);
		while (match.Success)
		{
			if (!this._wholeWord || WordBoundary.IsWholeWord(line, match.Index, match.Length))
			{
				return match.Index;
			}

			match = match.NextMatch();
		}

		return -1;
	}
}
