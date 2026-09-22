using FluentAssertions;
using FolderAssistant.Retrieval;

namespace FolderAssistant.Tests;

/// <summary>
/// Context assembly: what survives reranking, and what the budget stops.
///
/// <para>
/// Each case is built so the reducer's answer differs from the input order. A test whose expected output
/// is what cosine already gave would pass against a reducer that did nothing at all.
/// </para>
/// </summary>
public sealed class TokenBudgetContextReducerTests
{
	private readonly TokenBudgetContextReducer _reducer = new();

	[Fact]
	public void No_Candidates_Yields_Nothing()
		=> this._reducer.Reduce("anything", [], Budget()).Should().BeEmpty();

	/// <summary>
	/// The lexical half of the blend. Two passages the retrieval score cannot separate, where one actually
	/// contains the query's words: that one has to come first, or the lexical weight is doing nothing.
	/// </summary>
	[Fact]
	public void A_Passage_Containing_The_Query_Terms_Outranks_An_Equally_Scored_One_That_Does_Not()
	{
		RetrievalCandidate contains = Candidate("a.md", 0.60, "the harbour ferry timetable for weekday mornings");
		RetrievalCandidate lacks = Candidate("b.md", 0.60, "assorted notes about unrelated administrative matters");

		IReadOnlyList<ReducedPassage> reduced =
			this._reducer.Reduce("harbour ferry timetable", [lacks, contains], Budget());

		reduced.Should().HaveCount(2);
		reduced[0].Hit.FilePath.Should().Be("a.md", "it contains the query terms and the scores are tied");
	}

	/// <summary>
	/// The diversity half. A near-duplicate of an already-selected passage should lose to a slightly
	/// weaker passage that says something else — otherwise the budget is spent restating one idea.
	/// </summary>
	[Fact]
	public void A_Near_Duplicate_Loses_To_A_Weaker_Passage_That_Says_Something_Different()
	{
		RetrievalCandidate best = Candidate("first.md", 0.90, "the ferry crosses the harbour every weekday morning");
		RetrievalCandidate duplicate = Candidate("dup.md", 0.88, "the ferry crosses the harbour every weekday morning");
		RetrievalCandidate different = Candidate("other.md", 0.80, "parking permits are issued by the district office");

		IReadOnlyList<ReducedPassage> reduced =
			this._reducer.Reduce("ferry harbour", [best, duplicate, different], Budget(maxPassages: 2));

		reduced.Should().HaveCount(2);
		reduced[0].Hit.FilePath.Should().Be("first.md");
		reduced[1].Hit.FilePath.Should().Be(
			"other.md",
			"a passage repeating what is already selected adds nothing to the context");
	}

	/// <summary>Selection stops at the token budget rather than returning everything that ranked.</summary>
	[Fact]
	public void Selection_Stops_When_The_Token_Budget_Is_Spent()
	{
		RetrievalCandidate first = Candidate("a.md", 0.90, "alpha beta gamma", tokenCount: 60);
		RetrievalCandidate second = Candidate("b.md", 0.80, "delta epsilon zeta", tokenCount: 60);
		RetrievalCandidate third = Candidate("c.md", 0.70, "eta theta iota", tokenCount: 60);

		IReadOnlyList<ReducedPassage> reduced =
			this._reducer.Reduce("alpha", [first, second, third], Budget(maxTokens: 130));

		reduced.Should().HaveCount(2, "a third passage would exceed the budget");
	}

	/// <summary>
	/// A passage that overflows the budget on its own is still returned. Some context beats none, and an
	/// empty result would be indistinguishable from having found nothing at all.
	/// </summary>
	[Fact]
	public void The_Best_Passage_Is_Returned_Even_If_It_Alone_Exceeds_The_Budget()
	{
		RetrievalCandidate huge = Candidate("huge.md", 0.90, "alpha beta gamma", tokenCount: 5_000);

		IReadOnlyList<ReducedPassage> reduced = this._reducer.Reduce("alpha", [huge], Budget(maxTokens: 10));

		reduced.Should().ContainSingle();
		reduced[0].Hit.FilePath.Should().Be("huge.md");
	}

	/// <summary>
	/// A passage skipped for size does not end the scan: a smaller one after it can still fit. Stopping at
	/// the first overflow would make the result depend on the order candidates happened to arrive in.
	/// </summary>
	[Fact]
	public void A_Passage_Too_Large_To_Fit_Does_Not_Stop_A_Smaller_One_Following_It()
	{
		RetrievalCandidate small = Candidate("small.md", 0.90, "alpha beta", tokenCount: 5);
		RetrievalCandidate oversized = Candidate("big.md", 0.85, "gamma delta", tokenCount: 900);
		RetrievalCandidate alsoSmall = Candidate("also.md", 0.80, "epsilon zeta", tokenCount: 5);

		IReadOnlyList<ReducedPassage> reduced =
			this._reducer.Reduce("alpha", [small, oversized, alsoSmall], Budget(maxTokens: 20));

		reduced.Select(passage => passage.Hit.FilePath).Should().Contain("also.md");
		reduced.Select(passage => passage.Hit.FilePath).Should().NotContain("big.md");
	}

	[Fact]
	public void The_Passage_Cap_Bounds_The_Result()
	{
		List<RetrievalCandidate> many = [.. Enumerable.Range(0, 10)
			.Select(i => Candidate($"f{i}.md", 0.9 - (i * 0.01), $"passage number {i} about distinct subject {i}"))];

		this._reducer.Reduce("passage", many, Budget(maxPassages: 3)).Should().HaveCount(3);
	}

	/// <summary>
	/// The reported score is the blend that ordered the passage, not the cosine it arrived with. Reporting
	/// the raw score would explain a different ordering than the one actually produced.
	/// </summary>
	[Fact]
	public void The_Reported_Score_Is_The_Blended_Relevance_Not_The_Raw_Retrieval_Score()
	{
		RetrievalCandidate candidate = Candidate("a.md", 0.50, "harbour ferry timetable");

		IReadOnlyList<ReducedPassage> reduced = this._reducer.Reduce("harbour ferry", [candidate], Budget());

		reduced[0].Score.Should().BeGreaterThan(0.50, "full lexical coverage lifts a mid semantic score");
	}

	private static ContextBudget Budget(Int32 maxPassages = 5, Int32 maxTokens = 1_000)
		=> new(maxPassages, maxTokens);

	private static RetrievalCandidate Candidate(String path, Double score, String text, Int32 tokenCount = 10)
		=> new(new RetrievalHit($"chunk-{path}", path, 0, 0, tokenCount, score, "hash"), text, tokenCount);
}
