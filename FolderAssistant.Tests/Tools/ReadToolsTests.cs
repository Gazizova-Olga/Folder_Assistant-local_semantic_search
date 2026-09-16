using System.Text;
using FluentAssertions;
using FolderAssistant.Extraction;
using FolderAssistant.Tools;

namespace FolderAssistant.Tests.Tools;

/// <summary>
/// The read tools (SPEC-101), each asserted on its own arithmetic — numbering, bounds, what is hidden,
/// what is said in the note — and never through an end-to-end path that would still succeed on a
/// mangled result.
/// </summary>
public sealed class ReadToolsTests
{
	private const String Metadata = ".folderassistant";
	private const Int64 SearchFileBytes = 1_048_576;

	private static ReadTools Tools(TempFolder root)
		=> new(new WorkspacePathGuard(root.Path, Metadata), TextExtractorRegistry.Default, SearchFileBytes);

	// --- InspectDirectory ---------------------------------------------------------------------------

	[Fact]
	public void The_Listing_Is_The_Direct_Children_Sorted_By_Name()
	{
		using TempFolder root = new();
		File.WriteAllText(root.Combine("b.txt"), "bb");
		File.WriteAllText(root.Combine("a.txt"), "a");
		Directory.CreateDirectory(root.Combine("sub"));
		File.WriteAllText(root.Combine("sub", "nested.txt"), "nested");

		DirectoryListing listing = Tools(root).InspectDirectory(".");

		listing.Path.Should().Be(".");
		listing.Entries.Select(entry => entry.Name).Should().Equal("a.txt", "b.txt", "sub");
		listing.Entries[0].SizeBytes.Should().Be(1);
		listing.Entries[0].IsDirectory.Should().BeFalse();
		listing.Entries[2].IsDirectory.Should().BeTrue();
		listing.Entries[2].SizeBytes.Should().BeNull();
		listing.Truncated.Should().BeFalse();
		listing.Note.Should().BeNull();
	}

	[Fact]
	public void The_Listing_Hides_The_Metadata_Folder_And_Links_And_Counts_The_Links()
	{
		using TempFolder root = new();
		using TempFolder outside = new();
		Directory.CreateDirectory(root.Combine(Metadata));
		File.WriteAllText(root.Combine(Metadata, "manifest.db"), "db");
		File.WriteAllText(root.Combine("plain.txt"), "plain");
		LinkFixtures.CreateDirectoryLink(root.Combine("escape"), outside.Path);

		DirectoryListing listing = Tools(root).InspectDirectory(".");

		listing.Entries.Select(entry => entry.Name).Should().Equal("plain.txt");
		listing.Note.Should().Contain("1 link(s) hidden");
	}

	[Fact]
	public void The_Listing_Is_Cut_At_The_Entry_Bound_And_Says_So()
	{
		using TempFolder root = new();
		for (Int32 i = 0; i < ReadTools.MaxEntries + 3; i++)
		{
			File.WriteAllText(root.Combine($"f{i:D4}.txt"), "x");
		}

		DirectoryListing listing = Tools(root).InspectDirectory(".");

		listing.Entries.Should().HaveCount(ReadTools.MaxEntries);
		listing.Truncated.Should().BeTrue();
		listing.Note.Should().Contain($"cut at {ReadTools.MaxEntries}");
	}

	[Fact]
	public void The_Listing_Throws_For_A_File_Or_A_Missing_Directory()
	{
		using TempFolder root = new();
		File.WriteAllText(root.Combine("file.txt"), "x");
		ReadTools tools = Tools(root);

		Action file = () => tools.InspectDirectory("file.txt");
		Action missing = () => tools.InspectDirectory("nope");
		Action refused = () => tools.InspectDirectory(Metadata);

		file.Should().Throw<DirectoryNotFoundException>();
		missing.Should().Throw<DirectoryNotFoundException>();
		refused.Should().Throw<WorkspaceContainmentException>();
	}

	// --- ReadFile -----------------------------------------------------------------------------------

	[Fact]
	public void Lines_Are_Numbered_From_One_Whatever_The_Line_Ending()
	{
		using TempFolder root = new();
		File.WriteAllText(root.Combine("mixed.txt"), "one\r\ntwo\nthree");

		FileLines lines = Tools(root).ReadFile("mixed.txt");

		lines.Lines.Select(line => (line.Number, line.Text)).Should().Equal((1, "one"), (2, "two"), (3, "three"));
		lines.TotalLines.Should().Be(3);
		lines.Truncated.Should().BeFalse();
		lines.Note.Should().BeNull();
	}

	[Fact]
	public void A_Range_Returns_Exactly_Those_Lines_And_The_Files_Total()
	{
		using TempFolder root = new();
		File.WriteAllLines(root.Combine("ten.txt"), Enumerable.Range(1, 10).Select(i => $"line {i}"));

		FileLines lines = Tools(root).ReadFile("ten.txt", startLine: 4, endLine: 6);

		lines.Lines.Select(line => line.Number).Should().Equal(4, 5, 6);
		lines.Lines[0].Text.Should().Be("line 4");
		lines.TotalLines.Should().Be(10);
		lines.Truncated.Should().BeFalse();
	}

	[Fact]
	public void An_Empty_File_Has_No_Lines()
	{
		using TempFolder root = new();
		File.WriteAllText(root.Combine("empty.txt"), "");

		FileLines lines = Tools(root).ReadFile("empty.txt");

		lines.Lines.Should().BeEmpty();
		lines.TotalLines.Should().Be(0);
		lines.Truncated.Should().BeFalse();
		lines.Note.Should().Contain("empty");
	}

	[Fact]
	public void A_Byte_Order_Mark_Is_Consumed_Not_Returned()
	{
		using TempFolder root = new();
		File.WriteAllText(root.Combine("bom.txt"), "first\nsecond", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

		FileLines lines = Tools(root).ReadFile("bom.txt");

		lines.Lines[0].Text.Should().Be("first");
	}

	[Fact]
	public void A_Range_Past_The_End_Is_Not_Truncation()
	{
		using TempFolder root = new();
		File.WriteAllLines(root.Combine("three.txt"), ["a", "b", "c"]);

		FileLines lines = Tools(root).ReadFile("three.txt", startLine: 2, endLine: 50);

		lines.Lines.Select(line => line.Text).Should().Equal("b", "c");
		lines.Truncated.Should().BeFalse();
		lines.Note.Should().Contain("ends at line 3");
	}

	[Fact]
	public void A_Start_Past_The_End_Returns_No_Lines_And_A_Note()
	{
		using TempFolder root = new();
		File.WriteAllLines(root.Combine("three.txt"), ["a", "b", "c"]);

		FileLines lines = Tools(root).ReadFile("three.txt", startLine: 10);

		lines.Lines.Should().BeEmpty();
		lines.TotalLines.Should().Be(3);
		lines.Truncated.Should().BeFalse();
		lines.Note.Should().Contain("the file has 3 lines");
	}

	[Fact]
	public void The_Line_Bound_Cuts_The_Range_And_Names_The_Next_Line()
	{
		using TempFolder root = new();
		Int32 total = ReadTools.MaxLinesPerRead + 50;
		File.WriteAllLines(root.Combine("long.txt"), Enumerable.Range(1, total).Select(i => $"{i}"));

		FileLines lines = Tools(root).ReadFile("long.txt", startLine: 11);

		lines.Lines.Should().HaveCount(ReadTools.MaxLinesPerRead);
		lines.Lines[0].Number.Should().Be(11);
		lines.Lines[^1].Number.Should().Be(10 + ReadTools.MaxLinesPerRead);
		lines.TotalLines.Should().Be(total);
		lines.Truncated.Should().BeTrue();
		lines.Note.Should().Contain($"continue from line {11 + ReadTools.MaxLinesPerRead}");
	}

	[Fact]
	public void The_Character_Bound_Cuts_The_Range_Before_The_Line_Bound_Does()
	{
		using TempFolder root = new();
		String wide = new('x', ReadTools.MaxCharsPerRead / 4 + 1);
		File.WriteAllLines(root.Combine("wide.txt"), Enumerable.Repeat(wide, 10));

		FileLines lines = Tools(root).ReadFile("wide.txt");

		// Three fit; the fourth would cross the bound.
		lines.Lines.Should().HaveCount(3);
		lines.TotalLines.Should().Be(10);
		lines.Truncated.Should().BeTrue();
		lines.Note.Should().Contain("continue from line 4");
	}

	[Fact]
	public void One_Line_Wider_Than_The_Character_Bound_Is_Still_Returned()
	{
		// The best line is always returned, or a caller could never read past it.
		using TempFolder root = new();
		File.WriteAllText(root.Combine("huge-line.txt"), new String('y', ReadTools.MaxCharsPerRead + 10) + "\nnext");

		FileLines lines = Tools(root).ReadFile("huge-line.txt");

		lines.Lines.Should().HaveCount(1);
		lines.Truncated.Should().BeTrue();
		lines.Note.Should().Contain("continue from line 2");
	}

	[Fact]
	public void A_Bad_Range_Throws()
	{
		using TempFolder root = new();
		File.WriteAllText(root.Combine("f.txt"), "x");
		ReadTools tools = Tools(root);

		Action zero = () => tools.ReadFile("f.txt", startLine: 0);
		Action inverted = () => tools.ReadFile("f.txt", startLine: 5, endLine: 4);

		zero.Should().Throw<ArgumentOutOfRangeException>();
		inverted.Should().Throw<ArgumentOutOfRangeException>();
	}

	[Fact]
	public void A_Missing_File_A_Directory_And_A_Refused_Path_Throw()
	{
		using TempFolder root = new();
		Directory.CreateDirectory(root.Combine("dir"));
		ReadTools tools = Tools(root);

		Action missing = () => tools.ReadFile("nope.txt");
		Action directory = () => tools.ReadFile("dir");
		Action outside = () => tools.ReadFile(Path.Combine("..", "x.txt"));

		missing.Should().Throw<FileNotFoundException>();
		directory.Should().Throw<FileNotFoundException>().WithMessage("*is a directory*");
		outside.Should().Throw<WorkspaceContainmentException>();
	}

	[Fact]
	public void A_File_With_A_Nul_Byte_In_Its_First_Bytes_Is_Not_Text()
	{
		using TempFolder root = new();
		File.WriteAllBytes(root.Combine("blob.bin"), [0x50, 0x4B, 0x03, 0x04, 0x00, 0x41]);
		ReadTools tools = Tools(root);

		Action read = () => tools.ReadFile("blob.bin");
		Action retrieve = () => tools.Retrieve("blob.bin");

		read.Should().Throw<InvalidDataException>();
		retrieve.Should().Throw<InvalidDataException>();
	}

	[Fact]
	public void A_File_Above_The_Size_Bound_Is_Refused_Before_It_Is_Read()
	{
		using TempFolder root = new();
		using (FileStream stream = File.Create(root.Combine("big.txt")))
		{
			stream.SetLength(ReadTools.MaxFileBytes + 1);
		}

		Action read = () => Tools(root).ReadFile("big.txt");

		read.Should().Throw<IOException>().WithMessage("*above the*bound*");
	}

	// --- Retrieve -----------------------------------------------------------------------------------

	[Fact]
	public void A_Small_File_Is_Returned_Whole()
	{
		using TempFolder root = new();
		File.WriteAllText(root.Combine("small.txt"), "alpha\nbeta\n");

		FileText text = Tools(root).Retrieve("small.txt");

		text.Text.Should().Be("alpha\nbeta\n");
		text.TotalBytes.Should().Be(11);
		text.Truncated.Should().BeFalse();
		text.Note.Should().BeNull();
	}

	[Fact]
	public void A_Large_File_Is_Cut_At_The_Bound_With_Its_Size_Reported()
	{
		using TempFolder root = new();
		String content = new('z', ReadTools.MaxRetrieveChars + 100);
		File.WriteAllText(root.Combine("large.txt"), content);

		FileText text = Tools(root).Retrieve("large.txt");

		text.Text.Should().HaveLength(ReadTools.MaxRetrieveChars);
		text.TotalBytes.Should().Be(content.Length);
		text.Truncated.Should().BeTrue();
		text.Note.Should().Contain("ReadFile");
	}

	// --- FindFiles ----------------------------------------------------------------------------------

	[Fact]
	public void Matches_Come_Back_Relative_In_Walk_Order()
	{
		using TempFolder root = new();
		Directory.CreateDirectory(root.Combine("docs", "deep"));
		Directory.CreateDirectory(root.Combine("src"));
		File.WriteAllText(root.Combine("readme.md"), "");
		File.WriteAllText(root.Combine("docs", "b.md"), "");
		File.WriteAllText(root.Combine("docs", "a.md"), "");
		File.WriteAllText(root.Combine("docs", "deep", "c.md"), "");
		File.WriteAllText(root.Combine("src", "code.cs"), "");

		FileMatches all = Tools(root).FindFiles("*.md");
		FileMatches top = Tools(root).FindFiles("docs/*.md");

		// Each directory's files first, in name order, then its subdirectories in name order.
		all.Paths.Should().Equal(
			"readme.md",
			Path.Combine("docs", "a.md"),
			Path.Combine("docs", "b.md"),
			Path.Combine("docs", "deep", "c.md"));
		top.Paths.Should().Equal(Path.Combine("docs", "a.md"), Path.Combine("docs", "b.md"));
		all.Truncated.Should().BeFalse();
		all.Note.Should().BeNull();
	}

	[Fact]
	public void The_Walk_Skips_Ignored_Directories_The_Metadata_Folder_And_Links()
	{
		using TempFolder root = new();
		using TempFolder outside = new();
		File.WriteAllText(outside.Combine("secret.md"), "");
		Directory.CreateDirectory(root.Combine("bin"));
		Directory.CreateDirectory(root.Combine(".git"));
		Directory.CreateDirectory(root.Combine(Metadata));
		Directory.CreateDirectory(root.Combine("keep"));
		File.WriteAllText(root.Combine("bin", "built.md"), "");
		File.WriteAllText(root.Combine(".git", "HEAD.md"), "");
		File.WriteAllText(root.Combine(Metadata, "manifest.md"), "");
		File.WriteAllText(root.Combine("keep", "kept.md"), "");
		LinkFixtures.CreateDirectoryLink(root.Combine("escape"), outside.Path);

		FileMatches matches = Tools(root).FindFiles("*.md");

		matches.Paths.Should().Equal(Path.Combine("keep", "kept.md"));
	}

	[SymbolicLinkFact]
	public void The_Walk_Skips_A_Linked_File()
	{
		using TempFolder root = new();
		using TempFolder outside = new();
		File.WriteAllText(outside.Combine("secret.md"), "");
		File.WriteAllText(root.Combine("plain.md"), "");
		LinkFixtures.CreateFileLink(root.Combine("linked.md"), outside.Combine("secret.md"));

		FileMatches matches = Tools(root).FindFiles("*.md");

		matches.Paths.Should().Equal("plain.md");
	}

	[Fact]
	public void The_Walk_Stops_At_The_Match_Bound_And_Says_So()
	{
		using TempFolder root = new();
		for (Int32 i = 0; i < ReadTools.MaxMatches + 5; i++)
		{
			File.WriteAllText(root.Combine($"m{i:D4}.txt"), "");
		}

		FileMatches matches = Tools(root).FindFiles("*.txt");

		matches.Paths.Should().HaveCount(ReadTools.MaxMatches);
		matches.Truncated.Should().BeTrue();
		matches.Note.Should().Contain($"cut at {ReadTools.MaxMatches}");
	}

	[Fact]
	public void A_Parent_Naming_Pattern_Is_Refused()
	{
		using TempFolder root = new();

		Action act = () => Tools(root).FindFiles("../*.txt");

		act.Should().Throw<ArgumentException>();
	}

	// --- SearchText ---------------------------------------------------------------------------------

	[Fact]
	public void Matching_Lines_Come_Back_With_File_And_Line_In_Walk_Order()
	{
		using TempFolder root = new();
		Directory.CreateDirectory(root.Combine("docs"));
		File.WriteAllText(root.Combine("top.md"), "alpha\nneedle here\nomega");
		File.WriteAllText(root.Combine("docs", "inner.txt"), "first needle\nno\nneedle again, needle twice");
		File.WriteAllText(root.Combine("docs", "quiet.txt"), "nothing to see");

		TextSearchResult result = Tools(root).SearchText("needle");

		result.Pattern.Should().Be("needle");
		result.Matches.Select(match => (match.Path, match.Line, match.Text)).Should().Equal(
			("top.md", 2, "needle here"),
			(Path.Combine("docs", "inner.txt"), 1, "first needle"),
			(Path.Combine("docs", "inner.txt"), 3, "needle again, needle twice"));
		result.FilesSearched.Should().Be(3);
		result.Truncated.Should().BeFalse();
		result.Note.Should().BeNull();
	}

	[Fact]
	public void The_Search_Reads_Only_The_Scanners_Extensions_And_Skips_What_The_Walk_Skips()
	{
		using TempFolder root = new();
		using TempFolder outside = new();
		File.WriteAllText(outside.Combine("secret.txt"), "needle outside");
		Directory.CreateDirectory(root.Combine("bin"));
		Directory.CreateDirectory(root.Combine(Metadata));
		Directory.CreateDirectory(root.Combine("keep"));
		File.WriteAllText(root.Combine("bin", "built.txt"), "needle in bin");
		File.WriteAllText(root.Combine(Metadata, "manifest.txt"), "needle in metadata");
		File.WriteAllText(root.Combine("keep", "kept.txt"), "needle kept");
		File.WriteAllText(root.Combine("keep", "image.dat"), "needle in a format the index never reads");
		LinkFixtures.CreateDirectoryLink(root.Combine("escape"), outside.Path);

		TextSearchResult result = Tools(root).SearchText("needle");

		result.Matches.Select(match => match.Path).Should().Equal(Path.Combine("keep", "kept.txt"));
		result.FilesSearched.Should().Be(1);
	}

	[Fact]
	public void A_Path_Narrows_The_Search_And_A_Missing_Or_Refused_One_Throws()
	{
		using TempFolder root = new();
		Directory.CreateDirectory(root.Combine("a"));
		Directory.CreateDirectory(root.Combine("b"));
		File.WriteAllText(root.Combine("a", "one.txt"), "needle");
		File.WriteAllText(root.Combine("b", "two.txt"), "needle");
		ReadTools tools = Tools(root);

		TextSearchResult narrowed = tools.SearchText("needle", path: "b");
		Action missing = () => tools.SearchText("needle", path: "nope");
		Action file = () => tools.SearchText("needle", path: Path.Combine("a", "one.txt"));
		Action refused = () => tools.SearchText("needle", path: "..");

		narrowed.Matches.Select(match => match.Path).Should().Equal(Path.Combine("b", "two.txt"));
		missing.Should().Throw<DirectoryNotFoundException>();
		file.Should().Throw<DirectoryNotFoundException>();
		refused.Should().Throw<WorkspaceContainmentException>();
	}

	[Fact]
	public void Regex_Case_And_Whole_Word_Reach_The_Matcher()
	{
		using TempFolder root = new();
		File.WriteAllText(root.Combine("words.txt"), "Cat\nconcatenate\ncat food\nc.t");
		ReadTools tools = Tools(root);

		TextSearchResult literal = tools.SearchText("cat");
		TextSearchResult ignoreCase = tools.SearchText("cat", ignoreCase: true);
		TextSearchResult wholeWord = tools.SearchText("cat", wholeWord: true);
		TextSearchResult regex = tools.SearchText("^c.t$", regex: true);
		TextSearchResult regexIgnoreCase = tools.SearchText("^c.t$", regex: true, ignoreCase: true);
		Action badRegex = () => tools.SearchText("(", regex: true);
		Action empty = () => tools.SearchText("");

		literal.Matches.Select(match => match.Line).Should().Equal(2, 3);
		ignoreCase.Matches.Select(match => match.Line).Should().Equal(1, 2, 3);
		wholeWord.Matches.Select(match => match.Line).Should().Equal(3);
		regex.Matches.Select(match => match.Line).Should().Equal(4);
		regexIgnoreCase.Matches.Select(match => match.Line).Should().Equal(1, 4);
		badRegex.Should().Throw<ArgumentException>();
		empty.Should().Throw<ArgumentException>();
	}

	[Fact]
	public void A_Long_Line_Is_Shortened_Around_The_Match_And_Said_So()
	{
		using TempFolder root = new();
		String line = new String('a', 1000) + "needle" + new String('b', 1000);
		File.WriteAllText(root.Combine("wide.txt"), line + "\nshort needle");

		TextSearchResult result = Tools(root).SearchText("needle");

		result.Matches.Should().HaveCount(2);
		result.Matches[0].Text.Should().StartWith("…").And.EndWith("…").And.Contain("needle");
		result.Matches[0].Text.Should().HaveLength(ReadTools.MaxSearchLineChars + 2);
		result.Matches[1].Text.Should().Be("short needle");
		result.Truncated.Should().BeFalse();
		result.Note.Should().Contain("1 line(s) shortened");
	}

	[Fact]
	public void The_Line_Bound_Cuts_The_Search_And_Says_So()
	{
		using TempFolder root = new();
		File.WriteAllLines(root.Combine("many.txt"), Enumerable.Range(1, ReadTools.MaxSearchLines + 7).Select(i => $"needle {i}"));

		TextSearchResult result = Tools(root).SearchText("needle");

		result.Matches.Should().HaveCount(ReadTools.MaxSearchLines);
		result.Matches[^1].Line.Should().Be(ReadTools.MaxSearchLines);
		result.Truncated.Should().BeTrue();
		result.Note.Should().Contain($"cut at {ReadTools.MaxSearchLines} matching lines");
	}

	[Fact]
	public void The_Character_Bound_Cuts_The_Search_Before_The_Line_Bound_Does()
	{
		using TempFolder root = new();
		// Each matching line is shown at MaxSearchLineChars plus one ellipsis, so the payload bound is
		// crossed long before the line bound is.
		String wide = "needle" + new String('w', ReadTools.MaxSearchLineChars * 2);
		File.WriteAllLines(root.Combine("wide.txt"), Enumerable.Repeat(wide, ReadTools.MaxSearchLines));

		TextSearchResult result = Tools(root).SearchText("needle");

		Int32 shownLength = ReadTools.MaxSearchLineChars + 1;
		result.Matches.Should().HaveCount(ReadTools.MaxSearchChars / shownLength);
		result.Truncated.Should().BeTrue();
		result.Note.Should().Contain($"cut at {ReadTools.MaxSearchChars:N0} characters");
	}

	[Fact]
	public void The_Deadline_Returns_What_Was_Found_With_A_Note()
	{
		using TempFolder root = new();
		File.WriteAllText(root.Combine("a.txt"), "needle a\nneedle a again");
		File.WriteAllText(root.Combine("b.txt"), "needle b");
		ReadTools tools = new(new WorkspacePathGuard(root.Path, Metadata), TextExtractorRegistry.Default, SearchFileBytes, TimeSpan.Zero);

		TextSearchResult result = tools.SearchText("needle");

		// The deadline is checked after each line, so at most the first line of the first file is seen.
		result.Matches.Should().HaveCountLessThanOrEqualTo(1);
		result.FilesSearched.Should().Be(1);
		result.Truncated.Should().BeTrue();
		result.Note.Should().Contain("deadline");
	}

	[Fact]
	public void A_Cancelled_Search_Throws_Rather_Than_Returning_A_Partial_Result()
	{
		using TempFolder root = new();
		File.WriteAllText(root.Combine("a.txt"), "needle");
		using CancellationTokenSource cancelled = new();
		cancelled.Cancel();

		Action act = () => Tools(root).SearchText("needle", cancellationToken: cancelled.Token);

		act.Should().Throw<OperationCanceledException>();
	}

	[Fact]
	public void An_Oversize_File_And_A_File_Held_By_A_Writer_Are_Skipped_And_Counted()
	{
		using TempFolder root = new();
		File.WriteAllText(root.Combine("held.txt"), "needle held");
		File.WriteAllText(root.Combine("plain.txt"), "needle plain");
		using (FileStream big = File.Create(root.Combine("big.txt")))
		{
			big.SetLength(SearchFileBytes + 1);
		}

		using FileStream writer = new(root.Combine("held.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

		TextSearchResult result = Tools(root).SearchText("needle");

		result.Matches.Select(match => match.Path).Should().Equal("plain.txt");
		result.FilesSearched.Should().Be(2);
		result.Truncated.Should().BeFalse();
		result.Note.Should().Contain("1 file(s) above the").And.Contain("1 file(s) could not be read");
	}

	[Fact]
	public void A_Byte_Order_Mark_Does_Not_Reach_A_Matched_Line()
	{
		using TempFolder root = new();
		File.WriteAllText(root.Combine("bom.txt"), "needle first", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

		TextSearchResult result = Tools(root).SearchText("needle");

		result.Matches.Single().Text.Should().Be("needle first");
	}

	/// <summary>
	/// The registry decides which files the search reads, and it asks for more than "read at all": a
	/// format whose text sits inside a container is the index's to read through its extractor, and
	/// scanning its bytes raw would match inside the packaging. A registry that reads such a format is
	/// staged here, and the search must leave its files alone while still counting the plain ones.
	/// </summary>
	[Fact]
	public void The_Search_Reads_Only_Formats_That_Scan_As_Raw_Lines()
	{
		using TempFolder root = new();
		File.WriteAllText(root.Combine("plain.txt"), "needle plain");
		File.WriteAllText(root.Combine("packed.fake"), "needle inside a container");
		TextExtractorRegistry registry = new(new PlainTextExtractor(), new ContainerFormat(".fake"));
		ReadTools tools = new(new WorkspacePathGuard(root.Path, Metadata), registry, SearchFileBytes);

		TextSearchResult result = tools.SearchText("needle");

		registry.IsSupported(".fake").Should().BeTrue("the index reads the format; the search must still not scan it raw");
		result.Matches.Select(match => match.Path).Should().Equal("plain.txt");
		result.FilesSearched.Should().Be(1);
	}

	private sealed class ContainerFormat(String extension) : ITextExtractor
	{
		public IReadOnlyCollection<String> Extensions { get; } = [extension];

		public Boolean SupportsRawLineScanning => false;

		public String Extract(Byte[] bytes) => "extracted";
	}
}
