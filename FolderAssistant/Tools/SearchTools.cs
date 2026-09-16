using System.ComponentModel;
using FolderAssistant.Retrieval;

namespace FolderAssistant.Tools;

/// <summary>One file the index ranked for a query: its best passage's score, and how many of its passages ranked.</summary>
internal sealed record FileRelevance(String Path, Double Score, Int32 MatchingChunks);

/// <summary>The files a query ranked, best first. <paramref name="Note"/> says what was cut or why there is nothing.</summary>
internal sealed record FilesAbout(String Query, IReadOnlyList<FileRelevance> Files, Boolean Truncated, String? Note);

/// <summary>
/// The search tools (SPEC-101): what the index says about a query, at file level. Held apart from
/// <see cref="ReadTools"/> because a search tool's failure is a different thing from a file tool's: a
/// search that fails must end the turn, since a swallowed retrieval fault is indistinguishable from
/// "nothing relevant", and the holder is what a facade will tell the two apart by.
///
/// <para>
/// Every query goes through the composed <see cref="IRetrievalQuery"/> — the same telemetry decorator,
/// the same backend, the same index-readiness refusal the passage search has — rather than a second read
/// of every vector. A file-level search that scanned the store itself would be a second, unobserved,
/// always-full-scan path beside the measured one.
/// </para>
/// </summary>
internal sealed class SearchTools
{
	internal const Int32 MaxFiles = 20;
	internal const Int32 CandidatesPerFile = 5;
	internal const Int32 MaxCandidates = 100;

	private readonly IRetrievalQuery _query;
	private readonly String _databasePath;

	public SearchTools(IRetrievalQuery query, String databasePath)
	{
		ArgumentNullException.ThrowIfNull(query);
		ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
		this._query = query;
		this._databasePath = databasePath;
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
