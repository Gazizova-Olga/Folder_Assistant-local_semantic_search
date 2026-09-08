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
