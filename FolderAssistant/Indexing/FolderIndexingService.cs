using Microsoft.Extensions.Hosting;

namespace FolderAssistant.Indexing;

/// <summary>
/// Runs the folder index off the startup path.
///
/// <para>
/// Indexing used to run inline before the web host was built, so startup blocked for as long as a
/// full scan-chunk-embed pass took, and that cost scales with the analyzed folder rather than with
/// anything about the application. The database bootstrap stays where it was and still completes
/// before any request is handled; only the indexing moved. What a caller loses by that is the
/// guarantee that the index is populated when the host starts answering, which is what
/// <see cref="IndexState"/> exists to give back.
/// </para>
/// </summary>
internal sealed class FolderIndexingService : BackgroundService
{
	private readonly Func<IndexingResult> _runIndex;
	private readonly IndexState _state;

	public FolderIndexingService(Func<IndexingResult> runIndex, IndexState state)
	{
		ArgumentNullException.ThrowIfNull(runIndex);
		ArgumentNullException.ThrowIfNull(state);

		this._runIndex = runIndex;
		this._state = state;
	}

	protected override Task ExecuteAsync(CancellationToken stoppingToken)
		=> this.IndexAsync("startup", stoppingToken);

	private async Task IndexAsync(String reason, CancellationToken cancellationToken)
	{
		try
		{
			// The pipeline is synchronous and both CPU- and IO-bound. Handing it to the thread pool is
			// what keeps it off the thread the host is starting on.
			IndexingResult result = await Task.Run(this._runIndex, cancellationToken).ConfigureAwait(false);

			this._state.MarkReady(DateTime.UtcNow);

			Console.WriteLine(
				$"Indexing ({reason}): scanned={result.FilesScanned}, indexed={result.FilesIndexed}, " +
				$"unchanged={result.FilesUnchanged}, deleted={result.FilesDeleted}, " +
				$"chunks={result.ChunksIndexed}, vectors={result.VectorsIndexed}");
		}
		catch (OperationCanceledException)
		{
			// The host is shutting down. Not an indexing failure, and marking it as one would leave a
			// stopped process reporting a broken index.
			throw;
		}
		catch (Exception ex)
		{
			// Nothing else observes this pass. A background failure that only writes to a log leaves
			// retrieval answering as though the index were merely empty, so the state carries it.
			this._state.MarkFailed(ex);

			Console.WriteLine($"Indexing ({reason}) failed: {ex.Message}");
		}
	}
}
