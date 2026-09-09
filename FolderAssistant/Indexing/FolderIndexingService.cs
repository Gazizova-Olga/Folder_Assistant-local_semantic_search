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
///
/// <para>
/// After the first pass it keeps indexing from the change feed. Passes are serialized by the loop
/// itself: the next signal is not read until the current pass returns, so two passes cannot write
/// over each other however quickly the folder is being edited.
/// </para>
/// </summary>
internal sealed class FolderIndexingService : BackgroundService
{
	private readonly Func<IndexingResult> _runIndex;
	private readonly IFileChangeFeed? _changeFeed;
	private readonly IndexState _state;

	public FolderIndexingService(Func<IndexingResult> runIndex, IFileChangeFeed? changeFeed, IndexState state)
	{
		ArgumentNullException.ThrowIfNull(runIndex);
		ArgumentNullException.ThrowIfNull(state);

		this._runIndex = runIndex;
		this._changeFeed = changeFeed;
		this._state = state;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		await this.IndexAsync(isInitial: true, "startup", stoppingToken).ConfigureAwait(false);

		if (this._changeFeed is null)
		{
			return;
		}

		// Started after the first pass, not before it: signals raised while that pass was running
		// would describe a folder it has already read.
		this._changeFeed.Start();

		try
		{
			await foreach (FolderChangeSignal signal in
				this._changeFeed.ReadAllAsync(stoppingToken).ConfigureAwait(false))
			{
				await this.IndexAsync(isInitial: false, signal.Reason, stoppingToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
			// The host is shutting down.
		}
	}

	private async Task IndexAsync(Boolean isInitial, String reason, CancellationToken cancellationToken)
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

			// A refresh that fails must not tear down an index that is already serving queries; only
			// the initial build can leave the state unusable.
			if (isInitial)
			{
				return;
			}
		}
	}
}
