using FluentAssertions;
using FolderAssistant.Indexing;

namespace FolderAssistant.Tests;

/// <summary>
/// The scan is treated as the authoritative state of the folder, so a file it drops is a file the
/// next pass deletes from the index. That makes every filter here load-bearing rather than
/// cosmetic, and it is why none of them had a test until now.
/// </summary>
public sealed class LocalTextFileScannerTests
{
	private const Int64 MaxBytes = 1024;

	[Fact]
	public void A_File_Over_The_Size_Limit_Is_Excluded()
	{
		using TempFolder folder = new();
		File.WriteAllText(folder.Combine("big.md"), new String('a', (Int32)MaxBytes + 1));

		Scan(folder).Should().BeEmpty();
	}

	/// <summary>The limit is inclusive. Pinned because an off-by-one here silently drops a file.</summary>
	[Fact]
	public void A_File_Exactly_On_The_Size_Limit_Is_Included()
	{
		using TempFolder folder = new();
		File.WriteAllText(folder.Combine("exact.md"), new String('a', (Int32)MaxBytes));

		Scan(folder).Should().ContainSingle().Which.RelativePath.Should().Be("exact.md");
	}

	[Theory]
	[InlineData(".git")]
	[InlineData(".vs")]
	[InlineData("bin")]
	[InlineData("obj")]
	[InlineData("node_modules")]
	[InlineData(".folderassistant")]
	public void Generated_And_Metadata_Folders_Are_Not_Scanned(String ignored)
	{
		using TempFolder folder = new();
		Directory.CreateDirectory(folder.Combine(ignored));
		File.WriteAllText(folder.Combine(ignored, "inside.md"), "content");
		File.WriteAllText(folder.Combine("outside.md"), "content");

		Scan(folder).Should().ContainSingle().Which.RelativePath.Should().Be("outside.md");
	}

	/// <summary>
	/// The check is per directory name at every level, not just at the root. A `bin` two folders
	/// down is still build output.
	/// </summary>
	[Fact]
	public void Nested_Ignored_Folders_Are_Skipped_Too()
	{
		using TempFolder folder = new();
		Directory.CreateDirectory(folder.Combine("src", "app", "obj"));
		File.WriteAllText(folder.Combine("src", "app", "obj", "generated.cs"), "// generated");
		File.WriteAllText(folder.Combine("src", "app", "real.cs"), "// real");

		Scan(folder).Should().ContainSingle().Which.RelativePath.Should().Be("src/app/real.cs");
	}

	/// <summary>Relative paths are reported with forward slashes, whatever the platform separator is.</summary>
	[Fact]
	public void Nested_Paths_Are_Reported_With_Forward_Slashes()
	{
		using TempFolder folder = new();
		Directory.CreateDirectory(folder.Combine("docs", "guides"));
		File.WriteAllText(folder.Combine("docs", "guides", "start.md"), "content");

		Scan(folder).Should().ContainSingle().Which.RelativePath.Should().Be("docs/guides/start.md");
	}

	[Fact]
	public void An_Extension_Outside_The_Allowlist_Is_Excluded()
	{
		using TempFolder folder = new();
		File.WriteAllText(folder.Combine("image.png"), "not really a png");
		File.WriteAllText(folder.Combine("notes.md"), "content");

		Scan(folder).Should().ContainSingle().Which.RelativePath.Should().Be("notes.md");
	}

	/// <summary>Nothing to embed, so nothing to store. An empty chunk set is not worth a manifest row.</summary>
	[Fact]
	public void Empty_And_Whitespace_Only_Files_Are_Excluded()
	{
		using TempFolder folder = new();
		File.WriteAllText(folder.Combine("empty.md"), String.Empty);
		File.WriteAllText(folder.Combine("blank.md"), "   \r\n\t  ");

		Scan(folder).Should().BeEmpty();
	}

	/// <summary>
	/// The identity of a file is its path; the hash is what its content currently is. Delta handling
	/// depends on that split: an edit has to look like the same file with different content, not
	/// like a different file.
	/// </summary>
	[Fact]
	public void The_File_Id_Tracks_The_Path_And_The_Hash_Tracks_The_Content()
	{
		using TempFolder folder = new();
		File.WriteAllText(folder.Combine("notes.md"), "first");
		ScannedTextFile before = Scan(folder).Single();

		File.WriteAllText(folder.Combine("notes.md"), "second");
		ScannedTextFile after = Scan(folder).Single();

		after.FileId.Should().Be(before.FileId);
		after.FileHash.Should().NotBe(before.FileHash);
	}

	/// <summary>
	/// The load-bearing one. A scan that returned empty for a folder it could not read would be
	/// indistinguishable from a folder that is genuinely empty — and the next pass would delete the
	/// whole index on the strength of it.
	/// </summary>
	[Fact]
	public void An_Unreadable_Root_Fails_Loudly_Rather_Than_Reporting_An_Empty_Folder()
	{
		LocalTextFileScanner scanner = new();

		Func<Object> scan = () => scanner.Scan(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}"), MaxBytes);

		scan.Should().Throw<DirectoryNotFoundException>();
	}

	[Fact]
	public void A_Blank_Root_Is_Rejected()
	{
		LocalTextFileScanner scanner = new();

		Func<Object> scan = () => scanner.Scan("  ", MaxBytes);

		scan.Should().Throw<ArgumentException>();
	}

	private static IReadOnlyList<ScannedTextFile> Scan(TempFolder folder)
		=> new LocalTextFileScanner().Scan(folder.Path, MaxBytes);
}
