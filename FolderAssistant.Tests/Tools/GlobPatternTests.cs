using FluentAssertions;
using FolderAssistant.Tools;

namespace FolderAssistant.Tests.Tools;

/// <summary>
/// The glob rules as a table (SPEC-101): each pattern form against paths that should and should not
/// match. The one that matters most is the no-separator rule — <c>*.md</c> at any depth — because it is
/// the form a caller reaches for first and the one a path-anchored matcher gets wrong.
/// </summary>
public sealed class GlobPatternTests
{
	[Theory]
	// No separator: the file name, at any depth.
	[InlineData("*.md", "readme.md", true)]
	[InlineData("*.md", "docs/readme.md", true)]
	[InlineData("*.md", "docs/deep/er/readme.md", true)]
	[InlineData("*.md", "readme.txt", false)]
	[InlineData("*.md", "docs/readme.md.bak", false)]
	[InlineData("readme.md", "docs/readme.md", true)]
	[InlineData("read?e.md", "readme.md", true)]
	[InlineData("read?e.md", "readmme.md", false)]
	// A separator: the whole relative path.
	[InlineData("docs/*.md", "docs/readme.md", true)]
	[InlineData("docs/*.md", "docs/deep/readme.md", false)]
	[InlineData("docs/*.md", "readme.md", false)]
	[InlineData("docs/*.md", "otherdocs/readme.md", false)]
	// Double star: any number of segments, including none.
	[InlineData("**/*.cs", "a.cs", true)]
	[InlineData("**/*.cs", "src/a.cs", true)]
	[InlineData("**/*.cs", "src/deep/a.cs", true)]
	[InlineData("**/*.cs", "src/a.txt", false)]
	[InlineData("src/**/*.cs", "src/a.cs", true)]
	[InlineData("src/**/*.cs", "src/deep/er/a.cs", true)]
	[InlineData("src/**/*.cs", "srcx/a.cs", false)]
	[InlineData("src/**/*.cs", "a.cs", false)]
	[InlineData("src/**", "src/a.cs", true)]
	[InlineData("src/**", "src/deep/a.cs", true)]
	[InlineData("src/**", "other/a.cs", false)]
	[InlineData("**", "anything/at/all.txt", true)]
	// Backslashes are separators too; a leading separator is ignored.
	[InlineData(@"docs\*.md", "docs/readme.md", true)]
	[InlineData("/docs/*.md", "docs/readme.md", true)]
	// Regex characters in a pattern are literal.
	[InlineData("a+b.txt", "a+b.txt", true)]
	[InlineData("a+b.txt", "aab.txt", false)]
	[InlineData("(x).txt", "(x).txt", true)]
	public void Matches_As_The_Spec_Says(String pattern, String path, Boolean expected)
	{
		GlobPattern.Parse(pattern).IsMatch(path).Should().Be(expected, $"'{pattern}' against '{path}'");
	}

	[Fact]
	public void Case_Follows_The_Platform()
	{
		Boolean matched = GlobPattern.Parse("*.MD").IsMatch("docs/readme.md");

		matched.Should().Be(OperatingSystem.IsWindows());
	}

	[Fact]
	public void A_Path_With_Backslashes_Is_Matched_As_Separators()
	{
		GlobPattern.Parse("docs/*.md").IsMatch(@"docs\readme.md").Should().BeTrue();
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("/")]
	[InlineData("../*.md")]
	[InlineData("docs/../*.md")]
	public void An_Empty_Or_Parent_Naming_Pattern_Is_Refused(String pattern)
	{
		Action act = () => GlobPattern.Parse(pattern);

		act.Should().Throw<ArgumentException>();
	}
}
