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

		tokenized.ToStrings().Should().Equal("alpha", "beta", "gamma", "delta");
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
	/// <summary>
	/// The redundant trailing window. With seven tokens at size 4 and overlap 2, the step lands at
	/// token 6 and produces a window holding only the seventh token — which the previous chunk
	/// already contains in full. It is a suffix of its predecessor, not a passage, so it is dropped
	/// rather than embedded and stored.
	/// </summary>
	[Fact]
	public void A_Trailing_Window_Already_Covered_By_The_Previous_Chunk_Is_Dropped()
	{
		IReadOnlyList<TextChunk> chunks = Chunk(Tokens(7), chunkSize: 4, overlap: 2);

		chunks.Should().HaveCount(3);
		chunks[^1].TokenStart.Should().Be(4);
		chunks[^1].TokenEnd.Should().Be(7);
	}

	/// <summary>
	/// Dropping it must never drop a token. Swept rather than spot-checked, because the case only
	/// arises at particular alignments of token count against size and overlap — a single example
	/// proves nothing about the ones next to it.
	/// </summary>
	[Theory]
	[InlineData(4, 2)]
	[InlineData(4, 1)]
	[InlineData(4, 3)]
	[InlineData(3, 1)]
	[InlineData(5, 2)]
	[InlineData(8, 4)]
	public void Dropping_A_Trailing_Window_Never_Loses_A_Token(Int32 chunkSize, Int32 overlap)
	{
		for (Int32 count = 1; count <= 40; count++)
		{
			IReadOnlyList<TextChunk> chunks = Chunk(Tokens(count), chunkSize, overlap);

			chunks.Should().NotBeEmpty($"{count} tokens must produce at least one chunk");
			chunks[0].TokenStart.Should().Be(0);
			chunks[^1].TokenEnd.Should().Be(count, $"every token must be covered at {count} tokens");

			// No gap between one window and the next.
			chunks.Zip(chunks.Skip(1))
				.Should().OnlyContain(
					pair => pair.Second.TokenStart <= pair.First.TokenEnd,
					$"windows must not leave a gap at {count} tokens");
		}
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

		String a = chunker.Chunk("file-a", TokenizedText.FromTokens(tokens), 4, 0).Single().ChunkId;
		String b = chunker.Chunk("file-b", TokenizedText.FromTokens(tokens), 4, 0).Single().ChunkId;

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

	/// <summary>
	/// Chunk text is single-space-joined rather than sliced verbatim out of the source, and these
	/// ids pin that. They are not arbitrary: a chunk id is the hash of the chunk's text, so joining
	/// tokens even slightly differently renames every chunk in every folder already indexed — and
	/// the next pass would then delete the stored chunks while the unchanged-file check declined to
	/// re-embed their replacements, emptying the index without failing.
	///
	/// <para>
	/// The input carries a double space, a tab and a CRLF on purpose: those are the separators a
	/// verbatim slice would preserve and a normalised join collapses.
	/// </para>
	/// </summary>
	[Fact]
	public void Chunk_Ids_Are_Unchanged_By_How_The_Tokens_Are_Held()
	{
		const String prose = "The quick brown fox jumps over the lazy dog.\tSecond  line here\r\nthird line of prose";

		IReadOnlyList<TextChunk> chunks =
			new TextChunker().Chunk("golden-file", new SimpleTokenizer().Tokenize(prose), 6, 2);

		chunks.Select(chunk => chunk.Content).Should().Equal(
			"The quick brown fox jumps over",
			"jumps over the lazy dog. Second",
			"dog. Second line here third line",
			"third line of prose");

		chunks.Select(chunk => chunk.ChunkId).Should().Equal(
			"505dab920c7165bd74a9422bc29fd12e1d90a10d884a9bc9df156533b6d70017",
			"f610d565ac7f835da3b8f52dc447286a76e4f828f6dc776efcf3721d574e0d76",
			"2f5df5d73b64f0742aee9663956142d3b93626233335739a9ed1f901a362f7c1",
			"1f32a8a09be420b8c5e65dc24601b2cf281e776c0dc39e656297bc59f06db3ba");
	}

	/// <summary>
	/// The tokenizer's own output has to agree with the layout <see cref="TokenizedText.FromTokens"/>
	/// assumes, or every test built on that helper is exercising a shape the index never produces.
	/// </summary>
	[Fact]
	public void Tokenizing_And_Laying_Tokens_Back_Out_Agree_On_Chunk_Text()
	{
		const String prose = "one two three four five six seven";

		IReadOnlyList<TextChunk> fromSource = new TextChunker()
			.Chunk("file-1", new SimpleTokenizer().Tokenize(prose), 3, 1);
		IReadOnlyList<TextChunk> fromTokens = new TextChunker()
			.Chunk("file-1", TokenizedText.FromTokens(prose.Split(' ')), 3, 1);

		fromTokens.Select(chunk => chunk.ChunkId).Should().Equal(fromSource.Select(chunk => chunk.ChunkId));
	}

	private static IReadOnlyList<String> Tokens(Int32 count)
		=> Enumerable.Range(0, count).Select(i => $"t{i}").ToArray();

	private static IReadOnlyList<TextChunk> Chunk(IReadOnlyList<String> tokens, Int32 chunkSize, Int32 overlap)
		=> new TextChunker().Chunk("file-1", TokenizedText.FromTokens(tokens), chunkSize, overlap);
}
