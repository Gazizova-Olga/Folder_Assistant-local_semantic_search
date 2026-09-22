using System.Text;
using FluentAssertions;
using FolderAssistant.Extraction;
using FolderAssistant.Indexing;
using FolderAssistant.Retrieval;

namespace FolderAssistant.Tests;

/// <summary>
/// Snippet verification (SPEC-110): a rebuilt passage is the text that was embedded, or it is nothing.
/// The hits are made from the real chunker over the file's own text, so the hash a hit carries is the
/// one the index would record, and every mismatch below is a real change to the file after that.
/// </summary>
public sealed class PassageBuilderTests
{
	private const String Original = "alpha beta gamma\ndelta   epsilon zeta\r\neta theta iota kappa";

	[Fact]
	public void An_Unchanged_File_Rebuilds_Every_Chunk_Verified_With_The_Chunkers_Own_Join()
	{
		using TempFolder folder = new();
		Write(folder, "notes.md", Original);
		IReadOnlyList<RetrievalHit> hits = HitsOf("notes.md", Original, chunkSize: 4, overlap: 1);

		IReadOnlyList<RebuiltPassage> passages = Builder(folder).Rebuild(hits);

		passages.Should().HaveCount(hits.Count).And.OnlyContain(passage => passage.State == PassageState.Verified);
		passages[0].Text.Should().Be("alpha beta gamma delta");
		passages[1].Text.Should().Be("delta epsilon zeta eta");
		passages[0].TokenCount.Should().Be(4);
	}

	/// <summary>The defect this exists for: an edit that keeps the window in range yields the wrong text, and it must not be returned.</summary>
	[Fact]
	public void A_File_Edited_After_Indexing_Yields_No_Text_For_The_Changed_Window_And_Verified_Text_For_The_Rest()
	{
		using TempFolder folder = new();
		Write(folder, "notes.md", Original);
		IReadOnlyList<RetrievalHit> hits = HitsOf("notes.md", Original, chunkSize: 4, overlap: 0);
		Write(folder, "notes.md", Original.Replace("epsilon", "EPSILON"));

		IReadOnlyList<RebuiltPassage> passages = Builder(folder).Rebuild(hits);

		passages.Select(passage => passage.State).Should().Equal(PassageState.Verified, PassageState.Stale, PassageState.Verified);
		passages[1].Text.Should().BeNull();
		passages[1].Hit.Should().BeSameAs(hits[1]);
		passages[0].Text.Should().Be("alpha beta gamma delta");
	}

	[Fact]
	public void A_File_That_Shrank_Below_The_Window_Is_Stale_Not_An_Error()
	{
		using TempFolder folder = new();
		Write(folder, "notes.md", Original);
		IReadOnlyList<RetrievalHit> hits = HitsOf("notes.md", Original, chunkSize: 4, overlap: 0);
		Write(folder, "notes.md", "alpha beta");

		IReadOnlyList<RebuiltPassage> passages = Builder(folder).Rebuild(hits);

		passages.Should().OnlyContain(passage => passage.State == PassageState.Stale && passage.Text == null);
	}

	[Fact]
	public void A_Missing_File_And_A_Format_The_Registry_Does_Not_Hold_Are_Unavailable()
	{
		using TempFolder folder = new();
		Write(folder, "notes.md", Original);
		Write(folder, "sheet.bin", Original);
		RetrievalHit gone = HitsOf("notes.md", Original, chunkSize: 4, overlap: 0)[0] with { FilePath = "gone.md" };
		RetrievalHit unknown = HitsOf("sheet.bin", Original, chunkSize: 4, overlap: 0)[0];

		Builder(folder).Rebuild(gone).State.Should().Be(PassageState.Unavailable);
		Builder(folder).Rebuild(unknown).State.Should().Be(PassageState.Unavailable);
	}

	/// <summary>
	/// A container format's text is what its extractor says, never its bytes: the window is rebuilt from
	/// the extracted text and verifies against a hash taken over that same text.
	/// </summary>
	[Fact]
	public void A_Container_Format_Is_Rebuilt_From_Its_Extractors_Text_Not_Its_Bytes()
	{
		using TempFolder folder = new();
		File.WriteAllBytes(folder.Combine("doc.fake"), [0x50, 0x4B, 0x03, 0x04, 0xFF, 0x00]);
		TextExtractorRegistry registry = new(new PlainTextExtractor(), new FixedText(".fake", "one two three four"));
		IReadOnlyList<RetrievalHit> hits = HitsOf("doc.fake", "one two three four", chunkSize: 2, overlap: 0);

		IReadOnlyList<RebuiltPassage> passages = new PassageBuilder(folder.Path, registry).Rebuild(hits);

		passages.Select(passage => passage.Text).Should().Equal("one two", "three four");
	}

	[Fact]
	public void A_Sub_Folder_Path_Is_Resolved_With_The_Indexs_Own_Separator()
	{
		using TempFolder folder = new();
		Directory.CreateDirectory(folder.Combine("sub"));
		Write(folder, Path.Combine("sub", "notes.md"), Original);
		IReadOnlyList<RetrievalHit> hits = HitsOf("sub/notes.md", Original, chunkSize: 4, overlap: 0);

		Builder(folder).Rebuild(hits[0]).State.Should().Be(PassageState.Verified);
	}

	[Fact]
	public void The_Hash_A_Hit_Carries_Is_The_Chunkers()
	{
		TextChunk chunk = new TextChunker().Chunk("f", new SimpleTokenizer().Tokenize("a b c"), 3, 0)[0];

		chunk.ChunkHash.Should().Be(TextChunker.Sha256("a b c"));
		TextChunker.Sha256("a b c").Should().MatchRegex("^[0-9a-f]{64}$");
	}

	private static PassageBuilder Builder(TempFolder folder) => new(folder.Path, TextExtractorRegistry.Default);

	private static void Write(TempFolder folder, String name, String text)
		=> File.WriteAllText(folder.Combine(name), text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

	/// <summary>Hits as the index would record them: the real tokenizer and chunker over the text, the hash theirs.</summary>
	private static IReadOnlyList<RetrievalHit> HitsOf(String path, String text, Int32 chunkSize, Int32 overlap)
		=> [.. new TextChunker().Chunk("file", new SimpleTokenizer().Tokenize(text), chunkSize, overlap)
			.Select(chunk => new RetrievalHit(chunk.ChunkId, path, chunk.Index, chunk.TokenStart, chunk.TokenEnd, 0.9, chunk.ChunkHash))];

	private sealed class FixedText(String extension, String text) : ITextExtractor
	{
		public IReadOnlyCollection<String> Extensions { get; } = [extension];

		public Boolean SupportsRawLineScanning => false;

		public String Extract(Byte[] bytes) => text;
	}
}
