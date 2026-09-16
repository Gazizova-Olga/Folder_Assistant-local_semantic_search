using System.Text;

namespace FolderAssistant.Tools;

/// <summary>
/// Replaces every occurrence of a literal in a text in one pass, with the whole-word rule the search
/// uses (<see cref="WordBoundary"/>), so <c>Update</c> and <c>SearchText</c> cannot mean different
/// things by it.
///
/// <para>
/// Two cursors, and the distinction is the correctness of the whole thing: the <em>scan</em> cursor is
/// where the next search starts, and the <em>copy</em> cursor is how much of the original has been
/// carried into the output. A candidate that fails the word rule advances the scan and leaves the copy
/// where it was; had it moved the copy cursor too, the text between would never be written and the
/// skipped match would silently drop it.
/// </para>
/// </summary>
internal static class TextReplacer
{
	/// <summary>
	/// The text with every whole occurrence of <paramref name="find"/> replaced by
	/// <paramref name="replacement"/>; <paramref name="count"/> says how many. Matches do not overlap, and
	/// the replacement is never searched again.
	/// </summary>
	public static String Replace(String text, String find, String replacement, Boolean ignoreCase, Boolean wholeWord, out Int32 count)
	{
		ArgumentNullException.ThrowIfNull(text);
		ArgumentException.ThrowIfNullOrEmpty(find);
		ArgumentNullException.ThrowIfNull(replacement);

		StringComparison comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		StringBuilder output = new(text.Length);
		Int32 scan = 0;
		Int32 copied = 0;
		count = 0;

		while (scan < text.Length)
		{
			Int32 index = text.IndexOf(find, scan, comparison);
			if (index < 0)
			{
				break;
			}

			if (wholeWord && !WordBoundary.IsWholeWord(text, index, find.Length))
			{
				scan = index + 1;
				continue;
			}

			output.Append(text, copied, index - copied);
			output.Append(replacement);
			count++;
			copied = index + find.Length;
			scan = copied;
		}

		output.Append(text, copied, text.Length - copied);

		return count == 0 ? text : output.ToString();
	}
}
