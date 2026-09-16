namespace FolderAssistant.Tools;

/// <summary>
/// Where an inclusive line range sits in a text, as character offsets.
/// </summary>
/// <param name="Start">The offset of the first line's first character.</param>
/// <param name="End">
/// The offset just past the last line's terminator, or the end of the text when the last line has
/// none. The span <c>[Start, End)</c> is the whole of the range, terminators included.
/// </param>
/// <param name="EndsWithTerminator">Whether the last line of the range ended in a terminator.</param>
internal readonly record struct LineSpan(Int32 Start, Int32 End, Boolean EndsWithTerminator);

/// <summary>
/// Locates 1-based line ranges in a text by the same rule the line reader numbers them: <c>\n</c>,
/// <c>\r\n</c> and a lone <c>\r</c> each end a line, a final line with no terminator is still a line,
/// and an empty text has none. The rule is here, in one place with direct tests, because offset
/// arithmetic on line endings has failure modes a round trip through a file cannot see.
/// </summary>
internal static class LineRangeLocator
{
	/// <summary>How many lines <paramref name="text"/> has, as <c>ReadLine</c> would count them.</summary>
	public static Int32 CountLines(String text)
	{
		ArgumentNullException.ThrowIfNull(text);

		Int32 count = 0;
		Int32 index = 0;
		while (index < text.Length)
		{
			count++;
			index = NextLineStart(text, EndOfContent(text, index));
		}

		return count;
	}

	/// <summary>
	/// The span of lines <paramref name="startLine"/> through <paramref name="endLine"/>, inclusive.
	/// Throws when either line is past the end of the text, because writing to a line a file does not
	/// have is not a thing a caller can mean.
	/// </summary>
	public static LineSpan Locate(String text, Int32 startLine, Int32 endLine)
	{
		ArgumentNullException.ThrowIfNull(text);
		ArgumentOutOfRangeException.ThrowIfLessThan(startLine, 1);
		ArgumentOutOfRangeException.ThrowIfLessThan(endLine, startLine);

		Int32 number = 0;
		Int32 index = 0;
		Int32 start = -1;
		while (index < text.Length)
		{
			number++;
			Int32 contentEnd = EndOfContent(text, index);
			Int32 next = NextLineStart(text, contentEnd);

			if (number == startLine)
			{
				start = index;
			}

			if (number == endLine)
			{
				return new LineSpan(start, next, next > contentEnd);
			}

			index = next;
		}

		throw new ArgumentOutOfRangeException(
			start < 0 ? nameof(startLine) : nameof(endLine),
			$"the text has {number} line(s); lines {startLine}–{endLine} were asked for.");
	}

	/// <summary>
	/// The terminator the text uses, taken from its first line ending; <see cref="Environment.NewLine"/>
	/// when it has none to copy.
	/// </summary>
	public static String DetectNewLine(String text)
	{
		ArgumentNullException.ThrowIfNull(text);

		Int32 end = EndOfContent(text, 0);
		if (end >= text.Length)
		{
			return Environment.NewLine;
		}

		return text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? "\r\n" : text[end].ToString();
	}

	private static Int32 EndOfContent(String text, Int32 from)
	{
		Int32 index = from;
		while (index < text.Length && text[index] != '\n' && text[index] != '\r')
		{
			index++;
		}

		return index;
	}

	private static Int32 NextLineStart(String text, Int32 contentEnd)
	{
		if (contentEnd >= text.Length)
		{
			return contentEnd;
		}

		return text[contentEnd] == '\r' && contentEnd + 1 < text.Length && text[contentEnd + 1] == '\n'
			? contentEnd + 2
			: contentEnd + 1;
	}
}
