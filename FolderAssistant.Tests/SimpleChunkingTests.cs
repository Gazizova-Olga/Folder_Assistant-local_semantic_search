using FluentAssertions;
using FolderAssistant.Indexing;

namespace FolderAssistant.Tests;

/// <summary>
/// Window arithmetic is the one place an error is invisible from outside: a wrongly-chunked corpus
/// still indexes, still embeds, and still returns results — just worse ones, for reasons no test
/// further down the pipeline can attribute.
/// </summary>
public sealed class SimpleChunkingTests
{
	[Fact]
	public void Tokenizing_Splits_On_Whitespace_And_Nothing_Else()
	{
		TokenizedText tokenized = new SimpleTokenizer().Tokenize("alpha  beta\tgamma\r\ndelta");

		tokenized.Tokens.Should().Equal("alpha", "beta", "gamma", "delta");
	}

	[Theory]
	[InlineData("")]
	[InlineData("   \r\n\t ")]
	public void Blank_Content_Produces_No_Tokens(String content)
		=> new SimpleTokenizer().Tokenize(content).Tokens.Should().BeEmpty();

	/// <summary>
	/// The overlap is the whole point of the sliding window: a sentence straddling a boundary has to
	/// appear in full somewhere, or it is unretrievable.
	/// </summary>
	[Fact]
	public void Consecutive_Chunks_Overlap_By_The_Configured_Token_Count()
	{
		IReadOnlyList<TextChunk> chunks = Chunk(Tokens(10), chunkSize: 4, overlap: 2);

		chunks[0].TokenStart.Should().Be(0);
		chunks[0].TokenEnd.Should().Be(4);
		chunks[1].TokenStart.Should().Be(2);
		chunks[1].TokenEnd.Should().Be(6);

		// Each window starts `size - overlap` later than the one before it.
		chunks.Zip(chunks.Skip(1))
			.Should().OnlyContain(pair => pair.Second.TokenStart - pair.First.TokenStart == 2);
	}

	[Fact]
	public void Zero_Overlap_Partitions_The_Tokens_Without_Repeating_Any()
	{
		IReadOnlyList<TextChunk> chunks = Chunk(Tokens(9), chunkSize: 3, overlap: 0);

		chunks.Should().HaveCount(3);
		chunks.SelectMany(chunk => chunk.Content.Split(' ')).Should().OnlyHaveUniqueItems();
	}

	/// <summary>The last window is short, not padded. Padding would embed tokens the file does not have.</summary>
	[Fact]
	public void The_Final_Chunk_Is_Truncated_Rather_Than_Padded()
	{
		IReadOnlyList<TextChunk> chunks = Chunk(Tokens(5), chunkSize: 4, overlap: 0);

		chunks.Should().HaveCount(2);
		chunks[^1].TokenStart.Should().Be(4);
		chunks[^1].TokenEnd.Should().Be(5);
	}

	/// <summary>
	/// Documented rather than asserted-as-desirable. With seven tokens at size 4 and overlap 2, the
	/// last window is a single token the previous chunk already contains in full — and it still
	/// becomes its own chunk, costing an embedding and letting the same text surface twice in one
	/// result set. Recorded as an open question in SPEC-120; this pins the behaviour as it is, so
	/// that changing it is a deliberate act with a failing test attached.
	/// </summary>
	[Fact]
	public void A_Trailing_Window_Already_Covered_By_The_Overlap_Still_Becomes_Its_Own_Chunk()
	{
		IReadOnlyList<TextChunk> chunks = Chunk(Tokens(7), chunkSize: 4, overlap: 2);

		chunks[^1].TokenStart.Should().Be(6);
		chunks[^1].TokenEnd.Should().Be(7);
		chunks[^2].TokenStart.Should().Be(4);
		chunks[^2].TokenEnd.Should().Be(7);
	}

	[Fact]
	public void An_Empty_Token_List_Produces_No_Chunks()
		=> Chunk([], chunkSize: 4, overlap: 2).Should().BeEmpty();

	/// <summary>
	/// Chunk ids are content-addressed, which is what lets a re-index recognise an unchanged chunk
	/// rather than storing a second copy of it.
	/// </summary>
	[Fact]
	public void Identical_Content_At_The_Same_Index_Yields_The_Same_Chunk_Id()
	{
		IReadOnlyList<TextChunk> first = Chunk(Tokens(8), chunkSize: 4, overlap: 2);
		IReadOnlyList<TextChunk> second = Chunk(Tokens(8), chunkSize: 4, overlap: 2);

		second.Select(chunk => chunk.ChunkId).Should().Equal(first.Select(chunk => chunk.ChunkId));
	}

	[Fact]
	public void The_Same_Content_Under_A_Different_File_Id_Yields_Different_Chunk_Ids()
	{
		TextChunker chunker = new();
		IReadOnlyList<String> tokens = Tokens(4);

		String a = chunker.Chunk("file-a", tokens, 4, 0).Single().ChunkId;
		String b = chunker.Chunk("file-b", tokens, 4, 0).Single().ChunkId;

		a.Should().NotBe(b);
	}

	[Theory]
	[InlineData(0, 0)]
	[InlineData(-1, 0)]
	public void A_Non_Positive_Chunk_Size_Is_Rejected(Int32 chunkSize, Int32 overlap)
	{
		Func<Object> chunk = () => Chunk(Tokens(4), chunkSize, overlap);

		chunk.Should().Throw<ArgumentOutOfRangeException>();
	}

	/// <summary>
	/// An overlap equal to the chunk size would make the window never advance. The guard is what
	/// stops that becoming an infinite loop rather than an error.
	/// </summary>
	[Theory]
	[InlineData(4, 4)]
	[InlineData(4, 5)]
	[InlineData(4, -1)]
	public void An_Overlap_Outside_Zero_To_Chunk_Size_Is_Rejected(Int32 chunkSize, Int32 overlap)
	{
		Func<Object> chunk = () => Chunk(Tokens(8), chunkSize, overlap);

		chunk.Should().Throw<ArgumentOutOfRangeException>();
	}

	private static IReadOnlyList<String> Tokens(Int32 count)
		=> Enumerable.Range(0, count).Select(i => $"t{i}").ToArray();

	private static IReadOnlyList<TextChunk> Chunk(IReadOnlyList<String> tokens, Int32 chunkSize, Int32 overlap)
		=> new TextChunker().Chunk("file-1", tokens, chunkSize, overlap);
}
