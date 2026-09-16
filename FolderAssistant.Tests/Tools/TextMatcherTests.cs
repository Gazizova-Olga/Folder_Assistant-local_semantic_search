using FluentAssertions;
using FolderAssistant.Tools;

namespace FolderAssistant.Tests.Tools;

/// <summary>
/// The line matcher (SPEC-101) as a table: each form of pattern against lines that should and should
/// not match, with the index it should report — so the whole-word rule and the overlap handling are
/// pinned directly rather than through a search whose ranking would still look right on a wrong index.
/// </summary>
public sealed class TextMatcherTests
{
	[Theory]
	[InlineData("cat", false, false, false, "the cat sat", 4)]
	[InlineData("cat", false, false, false, "the dog sat", -1)]
	[InlineData("Cat", false, false, false, "the cat sat", -1)]
	[InlineData("Cat", false, true, false, "the cat sat", 4)]
	[InlineData("cat", false, false, true, "concatenate cat", 12)]
	[InlineData("cat", false, false, true, "concatenate cats", -1)]
	[InlineData("cat", false, false, true, "cat_food cat", 9)]
	[InlineData("cat", false, false, true, "(cat)", 1)]
	[InlineData("aa", false, false, true, "aaa aa", 4)]
	[InlineData("  ", false, false, false, "a  b", 1)]
	[InlineData("c.t", true, false, false, "the cot sat", 4)]
	[InlineData("c.t", false, false, false, "the cot sat", -1)]
	[InlineData("C[aeiou]T", true, true, false, "the cut", 4)]
	[InlineData("^the", true, false, false, "the the", 0)]
	[InlineData("^the", true, false, true, "these the", -1)]
	[InlineData("the", true, false, true, "these the", 6)]
	[InlineData("a+", true, false, true, "aaa_b aa", 6)]
	[InlineData(@"\d+", true, false, true, "v12 34", 4)]
	public void The_First_Whole_Match_Is_Reported(String pattern, Boolean regex, Boolean ignoreCase, Boolean wholeWord, String line, Int32 expected)
	{
		TextMatcher matcher = TextMatcher.Create(pattern, regex, ignoreCase, wholeWord);

		matcher.IndexIn(line).Should().Be(expected);
	}

	[Fact]
	public void A_Zero_Length_Regex_Match_Counts_As_A_Match()
	{
		// grep reports every line for 'x*'; so does this. The whole-word check with length zero is decided
		// by the characters either side of the position, which on an empty line is nothing.
		TextMatcher matcher = TextMatcher.Create("x*", regex: true, ignoreCase: false, wholeWord: true);

		matcher.IndexIn("").Should().Be(0);
	}

	[Fact]
	public void An_Empty_Pattern_And_A_Bad_Regex_Throw()
	{
		Action empty = () => TextMatcher.Create("", regex: false, ignoreCase: false, wholeWord: false);
		Action bad = () => TextMatcher.Create("(unclosed", regex: true, ignoreCase: false, wholeWord: false);
		Action literalBracket = () => TextMatcher.Create("(unclosed", regex: false, ignoreCase: false, wholeWord: false);

		empty.Should().Throw<ArgumentException>();
		bad.Should().Throw<ArgumentException>();
		literalBracket.Should().NotThrow();
	}

	[Theory]
	[InlineData('a', true)]
	[InlineData('Z', true)]
	[InlineData('7', true)]
	[InlineData('_', true)]
	[InlineData('é', true)]
	[InlineData('-', false)]
	[InlineData(' ', false)]
	[InlineData('.', false)]
	public void A_Word_Character_Is_A_Letter_A_Digit_Or_An_Underscore(Char c, Boolean expected)
	{
		WordBoundary.IsWordChar(c).Should().Be(expected);
	}
}
