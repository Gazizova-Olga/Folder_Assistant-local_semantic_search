namespace FolderAssistant.Tools;

/// <summary>
/// One rule for what a whole word is, so that every tool that offers <c>WholeWord</c> means the same
/// thing by it: a word character is a letter, a digit or an underscore, and a match is whole when
/// neither the character before it nor the one after it is one.
/// </summary>
internal static class WordBoundary
{
	public static Boolean IsWordChar(Char c)
		=> Char.IsLetterOrDigit(c) || c == '_';

	/// <summary>
	/// Whether the span of <paramref name="length"/> at <paramref name="index"/> in <paramref name="text"/>
	/// is bounded by non-word characters or by the ends of the text.
	/// </summary>
	public static Boolean IsWholeWord(String text, Int32 index, Int32 length)
		=> (index == 0 || !IsWordChar(text[index - 1]))
			&& (index + length >= text.Length || !IsWordChar(text[index + length]));
}
