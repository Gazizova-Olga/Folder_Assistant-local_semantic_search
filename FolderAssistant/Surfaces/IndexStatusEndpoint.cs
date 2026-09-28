using FolderAssistant.Indexing;
using FolderAssistant.Persistence;

namespace FolderAssistant.Surfaces;

/// <summary>One file the index gave up on, as a reader sees it.</summary>
internal sealed record FailedFileResponse(String Path, String? Error, Int32 Attempts);

/// <summary>
/// What the index is doing, for something that polls (<c>SPEC-120</c>).
///
/// <para>
/// <c>Failed</c> is exact and <c>FailedFiles</c> is a sample, so <c>FailedFilesTruncated</c> is not
/// optional: a sample read as the whole is how a folder with four hundred unindexed files looks like one
/// with ten.
/// </para>
/// </summary>
internal sealed record IndexStatusResponse(
	String Status,
	String? Error,
	DateTime? LastIndexedUtc,
	Int64 Delivered,
	Int64 Pending,
	Int64 Failed,
	IReadOnlyList<FailedFileResponse> FailedFiles,
	Boolean FailedFilesTruncated);

/// <summary>
/// <c>GET /api/index/status</c>: what a person or a script asks while waiting for a folder to be indexed.
///
/// <para>
/// <strong>"Still building" and "the build failed" are different answers</strong>, and keeping them apart
/// is the whole point of reporting a status at all: the first is the expected condition every process
/// passes through, and the second is a fault someone has to act on. The same distinction is why the
/// retrieval telemetry files them separately.
/// </para>
///
/// <para>
/// The counts are read through a read-only connection, because this is the endpoint that gets polled and
/// polling must not contend for the write lock the indexer needs to make the backlog go down.
/// </para>
/// </summary>
internal static class IndexStatusEndpoint
{
	/// <summary>How many failed files one response names. The count beside them is always exact.</summary>
	internal const Int32 MaxFailedFiles = 50;

	public static void MapIndexStatus(this WebApplication app)
	{
		ArgumentNullException.ThrowIfNull(app);

		app.MapGet("/api/index/status", (IIndexState state, IndexStatusReader reader) =>
		{
			IndexCounts counts = reader.Read(MaxFailedFiles);

			return Results.Ok(new IndexStatusResponse(
				state.Status.ToString(),
				// The message, not the exception: a stack trace on an endpoint is noise to whoever is
				// polling it and the log already has one.
				state.Error?.Message,
				state.LastIndexedUtc,
				counts.Delivered,
				counts.Pending,
				counts.Failed,
				[.. counts.FailedFiles.Select(static file => new FailedFileResponse(file.RelativePath, file.Error, file.Attempts))],
				counts.FailedFilesTruncated));
		});
	}
}
