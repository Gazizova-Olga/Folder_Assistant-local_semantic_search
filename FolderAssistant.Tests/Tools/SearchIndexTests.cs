using FluentAssertions;
using FolderAssistant.Extraction;
using FolderAssistant.Indexing;
using FolderAssistant.Retrieval;
using FolderAssistant.Tools;

namespace FolderAssistant.Tests.Tools;

/// <summary>
/// The passage search (SPEC-101): over a query answering with fixed hits and real files the hits were
/// chunked from, so every passage is verified for real. Asserted on the stages in order — over-fetch,
/// verification, the floor, the score-gap cutoff, the reduction — and on the memo the turn opens.
/// </summary>
public sealed class SearchIndexTests
{
	private const String Database = "db";

	[Fact]
	public void Passages_Come_Back_Verified_Best_First_With_Their_File_And_Text()
	{
		using Corpus corpus = new();
		FixedQuery query = new(
			corpus.Hit("a.md", 0, 0.80),
			corpus.Hit("b.md", 0, 0.95),
			corpus.Hit("a.md", 1, 0.70));

		SemanticSearchResult result = corpus.Tools(query).SearchIndex("what is alpha");

		result.Query.Should().Be("what is alpha");
		result.Passages.Select(passage => (passage.Path, passage.ChunkIndex)).Should().Equal(("b.md", 0), ("a.md", 0), ("a.md", 1));
		result.Passages[1].Text.Should().Be("alpha beta gamma delta");
		result.Passages.Should().BeInDescendingOrder(passage => passage.Score);
		result.Truncated.Should().BeFalse();
		result.Note.Should().BeNull();
	}

	[Fact]
	public void The_Query_Is_Over_Fetched_By_The_Multiplier_And_Capped()
	{
		using Corpus corpus = new();
		FixedQuery query = new();

		corpus.Tools(query).SearchIndex("topic", maxResults: 3);
		corpus.Tools(query).SearchIndex("topic", maxResults: SearchTools.MaxResults + 5);

		query.Calls[0].Options.TopK.Should().Be(3 * SearchTools.OverFetchMultiplier);
		// The largest request the bound allows, and the cap the pool could not exceed even if the constants moved.
		query.Calls[1].Options.TopK.Should().Be(Math.Min(SearchTools.MaxResults * SearchTools.OverFetchMultiplier, SearchTools.MaxCandidates));
	}

	/// <summary>The stage this tool exists for: a passage from a file that changed is withheld, and the note says so.</summary>
	[Fact]
	public void A_Passage_From_A_File_That_Changed_Is_Withheld_And_Said()
	{
		using Corpus corpus = new();
		FixedQuery query = new(corpus.Hit("a.md", 0, 0.9), corpus.Hit("b.md", 0, 0.8));
		corpus.Write("a.md", "alpha beta gamma DELTA epsilon zeta eta theta");

		SemanticSearchResult result = corpus.Tools(query).SearchIndex("alpha");

		result.Passages.Select(passage => passage.Path).Should().Equal("b.md");
		result.Note.Should().Contain("1 of 2 ranked passages withheld").And.Contain("changed since they were indexed");
	}

	[Fact]
	public void A_Missing_File_Is_Withheld_Under_Its_Own_Note_And_Every_Passage_Withheld_Is_Said()
	{
		using Corpus corpus = new();
		FixedQuery query = new(corpus.Hit("a.md", 0, 0.9) with { FilePath = "gone.md" });

		SemanticSearchResult result = corpus.Tools(query).SearchIndex("alpha");

		result.Passages.Should().BeEmpty();
		result.Note.Should().Contain("gone or could not be read").And.Contain("every ranked passage was withheld");
	}

	[Fact]
	public void No_Hits_Is_An_Empty_Result_With_A_Note()
	{
		using Corpus corpus = new();

		SemanticSearchResult result = corpus.Tools(new FixedQuery()).SearchIndex("nothing");

		result.Passages.Should().BeEmpty();
		result.Note.Should().Contain("no indexed passage ranked");
	}

	/// <summary>Refused and empty read differently: the score turned away is in the note.</summary>
	[Fact]
	public void A_Set_Whose_Best_Match_Is_Below_The_Floor_Is_Refused_Whole_With_The_Score()
	{
		using Corpus corpus = new();
		FixedQuery query = new(corpus.Hit("a.md", 0, 0.31), corpus.Hit("b.md", 0, 0.30));

		SemanticSearchResult result = corpus.Tools(query, relevanceFloor: 0.4).SearchIndex("unrelated");

		result.Passages.Should().BeEmpty();
		result.Note.Should().Contain("low confidence").And.Contain("0.310").And.Contain("0.400");
	}

	[Fact]
	public void The_Floor_Screens_The_Best_Verified_Match_Not_A_Withheld_One()
	{
		using Corpus corpus = new();
		FixedQuery query = new(corpus.Hit("a.md", 0, 0.9), corpus.Hit("b.md", 0, 0.3));
		corpus.Write("a.md", "changed entirely");

		SemanticSearchResult result = corpus.Tools(query, relevanceFloor: 0.4).SearchIndex("alpha");

		result.Passages.Should().BeEmpty();
		result.Note.Should().Contain("low confidence").And.Contain("0.300");
	}

	[Fact]
	public void Candidates_Far_Below_The_Best_Are_Dropped_By_The_Relative_Cutoff_And_Said()
	{
		using Corpus corpus = new();
		FixedQuery query = new(corpus.Hit("a.md", 0, 0.80), corpus.Hit("b.md", 0, 0.41), corpus.Hit("a.md", 1, 0.39));

		SemanticSearchResult result = corpus.Tools(query).SearchIndex("alpha", maxResults: 10);

		result.Passages.Select(passage => (passage.Path, passage.ChunkIndex)).Should().Equal(("a.md", 0), ("b.md", 0));
		result.Note.Should().Contain("1 candidates dropped for scoring under 50%");
	}

	[Fact]
	public void More_Candidates_Than_Asked_For_Are_Reduced_To_The_Count_And_Said()
	{
		using Corpus corpus = new();
		FixedQuery query = new(corpus.Hit("a.md", 0, 0.9), corpus.Hit("b.md", 0, 0.85), corpus.Hit("a.md", 1, 0.8));

		SemanticSearchResult result = corpus.Tools(query).SearchIndex("alpha", maxResults: 2);

		result.Passages.Should().HaveCount(2);
		result.Truncated.Should().BeTrue();
		result.Note.Should().Contain("2 of 3 candidates fit the budget of 2 passages");
	}

	[Fact]
	public void MaxResults_Past_The_Bound_Is_Cut_With_A_Note()
	{
		using Corpus corpus = new();

		SemanticSearchResult result = corpus.Tools(new FixedQuery()).SearchIndex("topic", maxResults: 99);

		result.Note.Should().Contain($"maxResults cut to {SearchTools.MaxResults}");
	}

	[Fact]
	public void A_Not_Ready_Index_Refuses_Through_The_Tool_Unchanged()
	{
		using Corpus corpus = new();
		SearchTools tools = corpus.Tools(new ThrowingQuery(new IndexNotReadyException("still building")));

		Action act = () => tools.SearchIndex("topic");

		act.Should().Throw<IndexNotReadyException>().WithMessage("still building");
	}

	[Fact]
	public void An_Identical_Call_Inside_A_Turns_Scope_Is_Answered_Once_And_Outside_It_Every_Time()
	{
		using Corpus corpus = new();
		FixedQuery query = new(corpus.Hit("a.md", 0, 0.9));
		SearchTools tools = corpus.Tools(query);

		SemanticSearchResult first;
		SemanticSearchResult second;
		SemanticSearchResult other;
		using (SearchMemo.BeginScope())
		{
			first = tools.SearchIndex("alpha", maxResults: 3);
			second = tools.SearchIndex("alpha", maxResults: 3);
			other = tools.SearchIndex("alpha", maxResults: 4);
		}

		tools.SearchIndex("alpha", maxResults: 3);
		tools.SearchIndex("alpha", maxResults: 3);

		second.Should().BeSameAs(first);
		other.Should().NotBeSameAs(first);
		query.Calls.Should().HaveCount(4);
		SearchMemo.IsOpen.Should().BeFalse();
	}

	[Fact]
	public void A_Scope_Does_Not_Outlive_Its_Turn_Or_Leak_Into_Another()
	{
		using Corpus corpus = new();
		FixedQuery query = new(corpus.Hit("a.md", 0, 0.9));
		SearchTools tools = corpus.Tools(query);

		using (SearchMemo.BeginScope())
		{
			tools.SearchIndex("alpha");
		}

		using (SearchMemo.BeginScope())
		{
			tools.SearchIndex("alpha");
		}

		query.Calls.Should().HaveCount(2);
	}

	/// <summary>Two files of eight tokens, chunked four at a time with no overlap, so each has two chunks.</summary>
	private sealed class Corpus : IDisposable
	{
		private const Int32 ChunkSize = 4;
		private readonly TempFolder _folder = new();
		private readonly Dictionary<String, IReadOnlyList<TextChunk>> _chunks = new(StringComparer.Ordinal);

		public Corpus()
		{
			this.Write("a.md", "alpha beta gamma delta epsilon zeta eta theta");
			this.Write("b.md", "one two three four five six seven eight");
		}

		public void Write(String name, String text)
		{
			File.WriteAllText(this._folder.Combine(name), text);
			this._chunks.TryAdd(name, new TextChunker().Chunk(name, new SimpleTokenizer().Tokenize(text), ChunkSize, 0));
		}

		public RetrievalHit Hit(String file, Int32 chunkIndex, Double score)
		{
			TextChunk chunk = this._chunks[file][chunkIndex];

			return new RetrievalHit(chunk.ChunkId, file, chunk.Index, chunk.TokenStart, chunk.TokenEnd, score, chunk.ChunkHash);
		}

		public SearchTools Tools(IRetrievalQuery query, Double relevanceFloor = RelevanceFloor.Off)
			=> new(query, Database, new PassageBuilder(this._folder.Path, TextExtractorRegistry.Default), new TokenBudgetContextReducer(), relevanceFloor);

		public void Dispose() => this._folder.Dispose();
	}

	private sealed record Call(String DatabasePath, String QueryText, RetrievalOptions Options);

	private sealed class FixedQuery(params RetrievalHit[] hits) : IRetrievalQuery
	{
		public List<Call> Calls { get; } = [];

		public IReadOnlyList<RetrievalHit> Search(String databasePath, String queryText, RetrievalOptions options)
		{
			this.Calls.Add(new Call(databasePath, queryText, options));

			return hits.Take(options.TopK).ToArray();
		}
	}

	private sealed class ThrowingQuery(Exception exception) : IRetrievalQuery
	{
		public IReadOnlyList<RetrievalHit> Search(String databasePath, String queryText, RetrievalOptions options)
			=> throw exception;
	}
}
