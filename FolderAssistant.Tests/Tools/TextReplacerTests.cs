using FluentAssertions;
using FolderAssistant.Tools;

namespace FolderAssistant.Tests.Tools;

/// <summary>
/// The replacer (SPEC-101) as a table: each flag against texts that should and should not change, with
/// the exact output and count — including the case the two-cursor rule exists for, a candidate skipped
/// by the whole-word check whose text must still reach the output.
/// </summary>
public sealed class TextReplacerTests
{
	[Theory]
	[InlineData("the cat sat on the cat", "cat", "dog", false, false, "the dog sat on the dog", 2)]
	[InlineData("the dog sat", "cat", "dog", false, false, "the dog sat", 0)]
	[InlineData("Cat cat CAT", "cat", "dog", false, false, "Cat dog CAT", 1)]
	[InlineData("Cat cat CAT", "cat", "dog", true, false, "dog dog dog", 3)]
	[InlineData("concatenate cat cats", "cat", "dog", false, true, "concatenate dog cats", 1)]
	[InlineData("concatenate", "cat", "dog", false, true, "concatenate", 0)]
	[InlineData("cat_food (cat)", "cat", "dog", false, true, "cat_food (dog)", 1)]
	[InlineData("aaa", "aa", "X", false, false, "Xa", 1)]
	[InlineData("aa", "a", "aa", false, false, "aaaa", 2)]
	[InlineData("remove this word", " this", "", false, false, "remove word", 1)]
	[InlineData("aaa aa", "aa", "X", false, true, "aaa X", 1)]
	[InlineData("line one\nline two", "line", "row", false, false, "row one\nrow two", 2)]
	public void Every_Whole_Occurrence_Is_Replaced_And_Counted(String text, String find, String replacement, Boolean ignoreCase, Boolean wholeWord, String expected, Int32 expectedCount)
	{
		String result = TextReplacer.Replace(text, find, replacement, ignoreCase, wholeWord, out Int32 count);

		result.Should().Be(expected);
		count.Should().Be(expectedCount);
	}

	[Fact]
	public void A_Skipped_Candidate_Keeps_Every_Character_Between_Matches()
	{
		// The failure this pins: a whole-word miss that moved the copy cursor along with the scan cursor
		// would drop "concatenate " from the output and still report one replacement.
		String result = TextReplacer.Replace("cat concatenate cat", "cat", "dog", ignoreCase: false, wholeWord: true, out Int32 count);

		result.Should().Be("dog concatenate dog");
		count.Should().Be(2);
	}

	[Fact]
	public void An_Empty_Find_Throws()
	{
		Action act = () => TextReplacer.Replace("text", "", "x", ignoreCase: false, wholeWord: false, out _);

		act.Should().Throw<ArgumentException>();
	}
}
