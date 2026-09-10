using FolderAssistant.Indexing;

namespace FolderAssistant.Retrieval;

/// <summary>One chunk that matched, and where to find its text.</summary>
internal sealed record RetrievalHit(
	String ChunkId,
	String FilePath,
	Int32 ChunkIndex,
	Int32 TokenStart,
	Int32 TokenEnd,
	Double Score);

/// <summary>How many hits to return, and how weak a match is still worth returning.</summary>
internal sealed record RetrievalOptions(Int32 TopK = 5, Double MinScore = 0.0);

/// <summary>
/// Thrown when the index cannot answer yet, rather than returning what has been indexed so far.
///
/// <para>
/// The alternative is worse than it looks. A search against a partly-built index succeeds, returns
/// hits, and gives a caller no way to tell those results apart from the ones a complete index would
/// have produced for a query it genuinely has nothing good for. Refusing is the only answer that
/// carries the difference, and it lets a caller wait or degrade deliberately instead of acting on a
/// result that is quietly wrong.
/// </para>
/// </summary>
internal sealed class IndexNotReadyException : InvalidOperationException
{
	public IndexNotReadyException(String message, Exception? innerException = null, Boolean isBuildFailure = false)
		: base(message, innerException)
	{
		this.IsBuildFailure = isBuildFailure;
	}

	/// <summary>
	/// Whether the index build itself failed, as opposed to not having finished yet.
	///
	/// <para>
	/// One exception type carries two conditions that read alike to a caller and must not read alike
	/// to an operator. An index that is still building is the ordinary state of every process before
	/// its first pass completes. An index that failed to build is a fault — usually a backend that is
	/// not answering — and nothing downstream can tell the two apart, because both refuse the query
	/// with the same type and neither carries a status of its own.
	/// </para>
	/// </summary>
	public Boolean IsBuildFailure { get; }
}

/// <summary>
/// A retrieval strategy.
///
/// <para>
/// Expressed as "return the top k" rather than "return every vector so the caller can rank them".
/// The difference matters: an implementation backed by a native nearest-neighbour index can push
/// the ranking down into the query engine, and no caller has to change for it. A contract that
/// handed back every candidate would make that implementation impossible to write.
/// </para>
/// </summary>
internal interface IRetrievalQuery
{
	IReadOnlyList<RetrievalHit> Search(String databasePath, String queryText, RetrievalOptions options);
}

/// <summary>
/// The index-state check every retrieval implementation owes its caller.
///
/// <para>
/// Shared rather than reimplemented per backend. An implementation that quietly forgot it would
/// answer from a half-built index, and results from a half-built index are indistinguishable from
/// genuinely poor ones — the failure would look like bad relevance, not like a missing check.
/// </para>
///
/// <para>
/// The check is skipped when no state was supplied, which is how a test or a caller that builds its
/// own store on the spot searches without standing up an indexing pass to satisfy.
/// </para>
/// </summary>
internal static class RetrievalGuard
{
	/// <summary>
	/// A background refresh leaves the index queryable — results are merely stale — so only Building
	/// and Failed block a query.
	/// </summary>
	public static void EnsureQueryable(IIndexState? indexState)
	{
		if (indexState is null)
		{
			return;
		}

		switch (indexState.Status)
		{
			case IndexStatus.Building:
				throw new IndexNotReadyException(
					"The folder index is still building; no vectors are queryable yet.");

			case IndexStatus.Failed:
				throw new IndexNotReadyException(
					$"The folder index failed to build: {indexState.Error?.Message}",
					indexState.Error,
					isBuildFailure: true);

			default:
				return;
		}
	}
}

/// <summary>
/// The outcome of a single search, as recorded in telemetry.
///
/// <para>
/// Three of these are not <see cref="Failed"/> on purpose. An error rate that counts an index that
/// has not finished building, or a query abandoned because the embedding backend never answered,
/// reports a broken retrieval backend on days when retrieval did nothing wrong — and the backends
/// are being compared against each other on exactly these numbers.
/// </para>
/// </summary>
internal enum RetrievalStatus
{
	Success,

	/// <summary>A genuine fault in the search itself.</summary>
	Failed,

	/// <summary>
	/// The index was not queryable yet. Every process passes through this before its first index
	/// finishes, so it is the one status a dashboard should expect to see and not act on.
	/// </summary>
	NotReady,

	/// <summary>
	/// The call was cancelled or timed out. A search embeds its query text before it can rank
	/// anything, so an embedding backend that stops answering ends the search rather than the
	/// ranking — a fault outside the backend whose latency this measurement exists to compare.
	/// </summary>
	TimedOut,
}

/// <summary>
/// One search's observability record.
///
/// <para>
/// The unit is the call, not the hit: a search ranks its whole candidate set in one pass, and it
/// succeeds or fails as a whole.
/// </para>
///
/// <para>
/// The fields are what a decorator can observe from outside <see cref="IRetrievalQuery"/>, and
/// deliberately no more. How large a candidate pool a backend read, and which model version it
/// scoped itself to, do not cross that interface — a wrapper reporting them would be reporting
/// what it assumed rather than what happened.
/// </para>
/// </summary>
internal sealed record RetrievalCallTelemetry(
	String Backend,
	Int32 RequestedTopK,
	Int32 ResultCount,
	Double LatencyMs,
	RetrievalStatus Status,
	String? ErrorCode = null);

/// <summary>
/// Sink for per-call retrieval telemetry, mirroring <see cref="Embedding.IEmbeddingTelemetry"/>: a
/// seam rather than a logging call on the retrieval path, so emission is testable and a metrics
/// backend can replace the default sink without the path itself changing.
/// </summary>
internal interface IRetrievalTelemetry
{
	void Record(RetrievalCallTelemetry call);
}
