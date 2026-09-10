using FolderAssistant.Embedding;
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
	private readonly IEmbeddingHealthCheck? _healthCheck;

	/// <summary>
	/// <paramref name="healthCheck"/> is null for every in-process embedder — they cannot be
	/// unreachable, so there is nothing to probe and the first pass simply begins.
	/// </summary>
	public FolderIndexingService(
		Func<IndexingResult> runIndex,
		IFileChangeFeed? changeFeed,
		IndexState state,
		IEmbeddingHealthCheck? healthCheck = null)
	{
		ArgumentNullException.ThrowIfNull(runIndex);
		ArgumentNullException.ThrowIfNull(state);

		this._runIndex = runIndex;
		this._changeFeed = changeFeed;
		this._state = state;
		this._healthCheck = healthCheck;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		await this.IndexAsync("startup", stoppingToken, probeFirst: true).ConfigureAwait(false);

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
				await this.IndexAsync(signal.Reason, stoppingToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
			// The host is shutting down.
		}
	}

	private async Task IndexAsync(String reason, CancellationToken cancellationToken, Boolean probeFirst = false)
	{
		try
		{
			if (probeFirst && this._healthCheck is not null)
			{
				// Before the first pass, not before every one: a backend that was reachable at startup
				// and has since died fails per file anyway, and re-probing on each refresh would add a
				// round-trip to every edit for an answer that is almost always yes.
				await this._healthCheck.CheckAsync(cancellationToken).ConfigureAwait(false);
			}

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
			// Keeping a failed refresh from tearing down a serving index is MarkFailed's job, not a
			// branch here: it refuses to leave Ready, so both callers report unconditionally.
			this._state.MarkFailed(ex);

			Console.WriteLine($"Indexing ({reason}) failed: {ex.Message}");
		}
	}
}
