using FluentAssertions;
using FolderAssistant.Tools;

namespace FolderAssistant.Tests.Tools;

/// <summary>
/// The line locator (SPEC-101) on its own arithmetic: the count and the offsets for each line-ending
/// form, a last line with no terminator, an empty text and a single line — pinned directly, because a
/// replacement that lands one character off still produces a file that reads plausibly.
/// </summary>
public sealed class LineRangeLocatorTests
{
	[Theory]
	[InlineData("", 0)]
	[InlineData("a", 1)]
	[InlineData("a\n", 1)]
	[InlineData("a\nb", 2)]
	[InlineData("a\r\nb\r\n", 2)]
	[InlineData("\n", 1)]
	[InlineData("\n\n", 2)]
	[InlineData("a\rb", 2)]
	[InlineData("a\r\n\r\nb", 3)]
	public void Lines_Are_Counted_As_The_Reader_Numbers_Them(String text, Int32 expected)
	{
		LineRangeLocator.CountLines(text).Should().Be(expected);
	}

	[Theory]
	[InlineData("a\nb\nc\nd", 2, 3, 2, 6, true)]
	[InlineData("a\r\nb\r\nc\r\nd", 2, 3, 3, 9, true)]
	[InlineData("a\nb\nc", 3, 3, 4, 5, false)]
	[InlineData("a\nb\nc\n", 3, 3, 4, 6, true)]
	[InlineData("only", 1, 1, 0, 4, false)]
	[InlineData("only\n", 1, 1, 0, 5, true)]
	[InlineData("a\nb\nc\nd", 1, 4, 0, 7, false)]
	[InlineData("a\rb\rc", 2, 2, 2, 4, true)]
	public void A_Range_Spans_From_Its_First_Character_Past_Its_Last_Terminator(String text, Int32 startLine, Int32 endLine, Int32 start, Int32 end, Boolean terminated)
	{
		LineSpan span = LineRangeLocator.Locate(text, startLine, endLine);

		span.Should().Be(new LineSpan(start, end, terminated));
	}

	[Fact]
	public void A_Range_Past_The_End_Throws_Naming_The_Count()
	{
		Action endPast = () => LineRangeLocator.Locate("a\nb\nc", 2, 4);
		Action startPast = () => LineRangeLocator.Locate("a\nb\nc", 4, 4);
		Action empty = () => LineRangeLocator.Locate("", 1, 1);

		endPast.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("endLine").WithMessage("*3 line(s)*");
		startPast.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("startLine");
		empty.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*0 line(s)*");
	}

	[Fact]
	public void A_Bad_Range_Throws_Before_The_Text_Is_Looked_At()
	{
		Action zero = () => LineRangeLocator.Locate("a", 0, 1);
		Action inverted = () => LineRangeLocator.Locate("a\nb", 2, 1);

		zero.Should().Throw<ArgumentOutOfRangeException>();
		inverted.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Theory]
	[InlineData("a\r\nb\nc", "\r\n")]
	[InlineData("a\nb\r\nc", "\n")]
	[InlineData("a\rb", "\r")]
	public void The_New_Line_Is_The_Texts_First(String text, String expected)
	{
		LineRangeLocator.DetectNewLine(text).Should().Be(expected);
	}

	[Fact]
	public void A_Text_With_No_Line_Ending_Takes_The_Platforms()
	{
		LineRangeLocator.DetectNewLine("single").Should().Be(Environment.NewLine);
		LineRangeLocator.DetectNewLine("").Should().Be(Environment.NewLine);
	}
}
