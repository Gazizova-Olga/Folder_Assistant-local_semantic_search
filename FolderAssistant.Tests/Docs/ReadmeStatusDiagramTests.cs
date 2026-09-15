using FluentAssertions;

namespace FolderAssistant.Tests.Docs;

/// <summary>
/// The README carries a copy of the implementation-status diagram, because the README is what a
/// visitor reads and GitHub renders the diagram there. A copy drifts; this pins the two blocks to be
/// identical, so a commit that moves a block in the source and not in the README fails here rather
/// than leaving the front page describing a tree that no longer exists.
/// </summary>
public sealed class ReadmeStatusDiagramTests
{
	[Fact]
	public void The_Readme_Diagram_Is_The_Status_Diagram()
	{
		String root = RepositoryRoot();
		String readme = MermaidBlock(Path.Combine(root, "README.md"));
		String source = MermaidBlock(Path.Combine(root, "docs", "diagrams", "implementation-status.md"));

		readme.Should().Be(source, "the README's copy of the status diagram must match its source");
	}

	private static String RepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FolderAssistant.slnx")))
		{
			directory = directory.Parent;
		}

		return directory?.FullName
			?? throw new InvalidOperationException("The repository root was not found above the test binaries.");
	}

	/// <summary>The first fenced <c>mermaid</c> block of a Markdown file, fence lines excluded, line endings normalised.</summary>
	private static String MermaidBlock(String path)
	{
		String[] lines = File.ReadAllLines(path);
		Int32 start = Array.IndexOf(lines, "```mermaid");
		start.Should().BeGreaterThanOrEqualTo(0, $"{path} must contain a mermaid block");

		Int32 end = Array.IndexOf(lines, "```", start + 1);
		end.Should().BeGreaterThan(start, $"the mermaid block in {path} must be closed");

		return String.Join('\n', lines, start + 1, end - start - 1);
	}
}
