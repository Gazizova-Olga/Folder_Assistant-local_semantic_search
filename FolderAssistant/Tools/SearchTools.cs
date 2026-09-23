using System.ComponentModel;
using System.Globalization;
using FolderAssistant.Retrieval;

namespace FolderAssistant.Tools;

/// <summary>One passage the index ranked for a query and the file it was read back from, its text verified against the chunk it was embedded as.</summary>
internal sealed record SemanticPassage(String Path, Int32 ChunkIndex, Double Score, String Text);

/// <summary>The passages a query selected, best first. <paramref name="Note"/> says what was cut, withheld or refused, and why there is nothing.</summary>
internal sealed record SemanticSearchResult(String Query, IReadOnlyList<SemanticPassage> Passages, Boolean Truncated, String? Note);

/// <summary>One file the index ranked for a query: its best passage's score, and how many of its passages ranked.</summary>
internal sealed record FileRelevance(String Path, Double Score, Int32 MatchingChunks);

/// <summary>The files a query ranked, best first. <paramref name="Note"/> says what was cut or why there is nothing.</summary>
internal sealed record FilesAbout(String Query, IReadOnlyList<FileRelevance> Files, Boolean Truncated, String? Note);

/// <summary>
/// The search tools (SPEC-101): what the index says about a query — the passages that answer it, verified
/// and fitted to a budget, or the files that are about it. Held apart from
/// <see cref="ReadTools"/> because a search tool's failure is a different thing from a file tool's: a
/// search that fails must end the turn, since a swallowed retrieval fault is indistinguishable from
/// "nothing relevant", and the holder is what a facade will tell the two apart by.
///
/// <para>
/// Every query goes through the composed <see cref="IRetrievalQuery"/> — the same telemetry decorator,
/// the same backend, the same index-readiness refusal — rather than a second read of every vector. A file-level search that scanned the store itself would be a second, unobserved,
/// always-full-scan path beside the measured one.
/// </para>
/// </summary>
internal sealed class SearchTools
{
	internal const Int32 MaxFiles = 20;
	internal const Int32 CandidatesPerFile = 5;
	internal const Int32 MaxCandidates = 100;

	/// <summary>The most passages one search returns; a request past it is cut and the cut is said.</summary>
	internal const Int32 MaxResults = 20;

	/// <summary>How many passages are ranked per passage asked for, before the screen, the cutoff and the reduction.</summary>
	internal const Int32 OverFetchMultiplier = 4;

	/// <summary>
	/// The context budget one search may fill, in the chunker's tokens — about eight whole chunks at the
	/// shipped chunk size. A code constant, as SPEC-110 defers per-call budget configuration.
	/// </summary>
	internal const Int32 MaxContextTokens = 2_000;

	/// <summary>
	/// The relative score-gap cutoff: a candidate scoring below this fraction of the query's best hit is
	/// dropped before reduction. The model chooses <c>maxResults</c> and does not reliably ask for few, so
	/// the count cannot be trusted to bound relevance; an absolute floor cannot either, because the scores
	/// are the embedder's and differ by model (SPEC-110). This query's own best hit is the one scale both
	/// have in common. The fraction is unmeasured — a candidate at half the best cosine is far from it under
	/// every embedder measured here — and is the constant to tune from <c>SemanticSearchBenchmark</c>.
	/// </summary>
	internal const Double ScoreGapFraction = 0.5;

	private readonly IRetrievalQuery _query;
	private readonly String _databasePath;
	private readonly PassageBuilder _passages;
	private readonly IContextReduction _reducer;
	private readonly Double _relevanceFloor;

	/// <param name="relevanceFloor">The low-confidence floor (SPEC-110); <see cref="RelevanceFloor.Off"/> unless the embedder's scores have been measured to support one.</param>
	public SearchTools(IRetrievalQuery query, String databasePath, PassageBuilder passages, IContextReduction reducer, Double relevanceFloor = RelevanceFloor.Off)
	{
		ArgumentNullException.ThrowIfNull(query);
		ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
		ArgumentNullException.ThrowIfNull(passages);
		ArgumentNullException.ThrowIfNull(reducer);
		ArgumentOutOfRangeException.ThrowIfNegative(relevanceFloor);
		this._query = query;
		this._databasePath = databasePath;
		this._passages = passages;
		this._reducer = reducer;
		this._relevanceFloor = relevanceFloor;
	}

	[Description("Finds the passages in the workspace's files that best answer a question or match a topic, by meaning rather than exact words, and returns their text with the file and a relevance score. Every passage is the file's current text, verified to be what was indexed; a passage from a file that changed since is withheld and the note says so. Refuses while the first index is still building.")]
	public SemanticSearchResult SearchIndex(
		[Description("The question or topic, in full.")] String query,
		[Description("How many passages to return at most, best first. Defaults to 5; at most 20. Fewer come back when the rest are far weaker than the best.")] Int32 maxResults = 5)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(query);
		ArgumentOutOfRangeException.ThrowIfLessThan(maxResults, 1);

		// One answer per identical call within a turn. The memo holds the result, refusal included, so a
		// retry after the model misread the note costs nothing and cannot come back different.
		if (SearchMemo.TryGet(query, maxResults, out SemanticSearchResult? memoized))
		{
			return memoized!;
		}

		SemanticSearchResult result = this.Search(query, maxResults);
		SearchMemo.Set(query, maxResults, result);

		return result;
	}

	private SemanticSearchResult Search(String query, Int32 maxResults)
	{
		List<String> notes = [];
		Int32 wanted = maxResults;
		if (wanted > MaxResults)
		{
			wanted = MaxResults;
			notes.Add($"maxResults cut to {MaxResults}");
		}

		// Over-fetched, because the screen, the cutoff and the reducer each drop candidates and the caller
		// asked for a count of what survives; bounded, so the query stays a top-k and not a scan.
		Int32 topK = Math.Min(wanted * OverFetchMultiplier, MaxCandidates);
		IReadOnlyList<RetrievalHit> hits = this._query.Search(this._databasePath, query, new RetrievalOptions(TopK: topK));
		if (hits.Count == 0)
		{
			return new SemanticSearchResult(query, [], false, Join(notes, "no indexed passage ranked for this query: the index may hold no vectors for the active model yet"));
		}

		// Every passage is rebuilt and verified before anything below sees it (SPEC-110). What is not the
		// text that was embedded is not a candidate, and the note says how many were withheld and why.
		IReadOnlyList<RebuiltPassage> rebuilt = this._passages.Rebuild(hits);
		List<RetrievalCandidate> candidates = [.. rebuilt
			.Where(static passage => passage.State == PassageState.Verified)
			.Select(static passage => new RetrievalCandidate(passage.Hit, passage.Text!, passage.TokenCount))];
		Int32 stale = rebuilt.Count(static passage => passage.State == PassageState.Stale);
		Int32 unavailable = rebuilt.Count(static passage => passage.State == PassageState.Unavailable);
		if (stale > 0)
		{
			notes.Add($"{stale} of {rebuilt.Count} ranked passages withheld: their files changed since they were indexed and the index has not caught up");
		}

		if (unavailable > 0)
		{
			notes.Add($"{unavailable} of {rebuilt.Count} ranked passages withheld: their files are gone or could not be read");
		}

		if (candidates.Count == 0)
		{
			return new SemanticSearchResult(query, [], false, Join(notes, "every ranked passage was withheld; try again once the index has caught up, or use SearchText"));
		}

		// The low-confidence screen, on the best verified match: a set whose best is weak is refused whole,
		// and refused reads differently from empty, with the score that was turned away.
		ConfidenceScreening screening = RelevanceFloor.Screen(candidates, this._relevanceFloor);
		if (screening.IsBelowFloor)
		{
			return new SemanticSearchResult(query, [], false, Join(notes, String.Create(CultureInfo.InvariantCulture, $"low confidence: the best match scored {screening.BestScore:F3}, below the floor {this._relevanceFloor:F3}; the folder may hold nothing on this")));
		}

		// The relative score-gap cutoff, against this query's own best hit; the reducer then spends the
		// budget on what is left.
		Double cutoff = screening.BestScore * ScoreGapFraction;
		Int32 before = candidates.Count;
		candidates.RemoveAll(candidate => candidate.Hit.Score < cutoff);
		if (candidates.Count < before)
		{
			notes.Add(String.Create(CultureInfo.InvariantCulture, $"{before - candidates.Count} candidates dropped for scoring under {ScoreGapFraction * 100:F0}% of the best match"));
		}

		IReadOnlyList<ReducedPassage> selected = this._reducer.Reduce(query, candidates, new ContextBudget(wanted, MaxContextTokens));

		Boolean truncated = candidates.Count > selected.Count;
		if (truncated)
		{
			notes.Add($"{selected.Count} of {candidates.Count} candidates fit the budget of {wanted} passages and {MaxContextTokens} tokens");
		}

		List<SemanticPassage> passages = [.. selected.Select(static passage => new SemanticPassage(
			passage.Hit.FilePath.Replace('/', Path.DirectorySeparatorChar),
			passage.Hit.ChunkIndex,
			passage.Score,
			passage.Text))];

		return new SemanticSearchResult(query, passages, truncated, Join(notes, null));
	}

	private static String? Join(List<String> notes, String? last)
	{
		if (last is not null)
		{
			notes.Add(last);
		}

		return notes.Count == 0 ? null : String.Join("; ", notes);
	}

	[Description("Finds the files in the workspace whose content is most about a topic, by meaning rather than exact words. Returns each file with a relevance score and how many of its passages matched. Use ReadFile on a result to see the text. Refuses while the first index is still building.")]
	public FilesAbout FindFilesAbout(
		[Description("What the files should be about, as a question or a phrase.")] String query,
		[Description("How many files to return, best first. Defaults to 10; at most 20.")] Int32 maxFiles = 10)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(query);
		ArgumentOutOfRangeException.ThrowIfLessThan(maxFiles, 1);

		List<String> notes = [];
		Int32 wanted = maxFiles;
		if (wanted > MaxFiles)
		{
			wanted = MaxFiles;
			notes.Add($"maxFiles cut to {MaxFiles}");
		}

		// Over-fetched by passages, because the seam ranks passages and a file is as relevant as its best
		// one. A file whose every passage ranks below the pool is invisible here; the pool is bounded so
		// the query stays the passage search's own query with a larger k, not a scan.
		Int32 topK = Math.Min(wanted * CandidatesPerFile, MaxCandidates);
		IReadOnlyList<RetrievalHit> hits = this._query.Search(this._databasePath, query, new RetrievalOptions(TopK: topK));

		List<FileRelevance> ranked = [.. hits
			.GroupBy(static hit => hit.FilePath, StringComparer.Ordinal)
			.Select(static group => new FileRelevance(
				group.Key.Replace('/', Path.DirectorySeparatorChar),
				group.Max(static hit => hit.Score),
				group.Count()))
			.OrderByDescending(static file => file.Score)
			.ThenBy(static file => file.Path, StringComparer.Ordinal)];

		Boolean truncated = ranked.Count > wanted;
		if (truncated)
		{
			notes.Add($"cut at {wanted} files; the {hits.Count} best passages spanned {ranked.Count}");
			ranked.RemoveRange(wanted, ranked.Count - wanted);
		}
		else if (hits.Count == topK)
		{
			notes.Add($"ranked from the {topK} best passages; a file whose passages all rank lower is not listed");
		}

		if (ranked.Count == 0)
		{
			notes.Add("no indexed passage ranked for this query: the index may hold no vectors for the active model yet");
		}

		return new FilesAbout(query, ranked, truncated, notes.Count == 0 ? null : String.Join("; ", notes));
	}
}
