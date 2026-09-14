using FolderAssistant.Embedding;
using Microsoft.Extensions.Hosting;

namespace FolderAssistant.Indexing;

/// <summary>
/// Runs the folder index off the startup path: one whole-folder pass, and then the front end that
/// keeps the index in step with the folder.
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
/// <strong>The whole-folder pass comes first and runs once.</strong> A corpus-fitted embedder has to
/// see the entire corpus before it can embed anything, and a cold folder is cheapest to embed in one
/// batched pass. The pass records the folder through the front end's own comparison before it embeds
/// anything, so every file row is the store's whichever path wrote it; what the pass adds is the
/// chunks and vectors, and the mark saying they were delivered. After it the front end
/// (<see cref="IFolderIndexer"/>) takes over — it watches, reconciles and delivers one changed file
/// at a time — and nothing runs the whole-folder pass again while it runs, because the pass embeds
/// against a snapshot of the record and the loops move the record.
/// </para>
/// </summary>
internal sealed class FolderIndexingService : BackgroundService
{
	private readonly Func<IndexingResult> _runIndex;
	private readonly IFolderIndexer? _indexer;
	private readonly IndexState _state;
	private readonly IEmbeddingHealthCheck? _healthCheck;
	private readonly TimeSpan _failedRetryInterval;

	private Boolean _probePassed;

	/// <param name="indexer">
	/// The front end, started once the first pass has succeeded and stopped with the host. Null runs
	/// the pass alone, for a caller that wants nothing incremental.
	/// </param>
	/// <param name="healthCheck">
	/// Null for every in-process embedder — they cannot be unreachable, so there is nothing to probe
	/// and the first pass simply begins.
	/// </param>
	/// <param name="failedRetryInterval">
	/// How long to wait before trying a failed first pass again. Zero, the default here, means the
	/// first attempt is the only one — a caller says how long it wants to keep trying rather than
	/// inheriting a schedule.
	/// </param>
	public FolderIndexingService(
		Func<IndexingResult> runIndex,
		IFolderIndexer? indexer,
		IndexState state,
		IEmbeddingHealthCheck? healthCheck = null,
		TimeSpan failedRetryInterval = default)
	{
		ArgumentNullException.ThrowIfNull(runIndex);
		ArgumentNullException.ThrowIfNull(state);

		this._runIndex = runIndex;
		this._indexer = indexer;
		this._state = state;
		this._healthCheck = healthCheck;
		this._failedRetryInterval = failedRetryInterval;
	}

	protected override Task ExecuteAsync(CancellationToken stoppingToken)
		=> this.RunFirstPassUntilItSucceedsAsync(stoppingToken);

	public override async Task StopAsync(CancellationToken cancellationToken)
	{
		// The attempt loop is told to stop before the front end is, never after: the front end is
		// started at the end of a successful attempt, and stopping it while one could still start it
		// would leave it running past the host.
		await base.StopAsync(cancellationToken).ConfigureAwait(false);

		if (this._indexer is not null)
		{
			await this._indexer.StopAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Runs the first attempt, and keeps running it while it fails.
	///
	/// <para>
	/// What fails a first index is usually outside this process and usually temporary: an embedding
	/// backend still starting, a model still being pulled. Without this the state says
	/// <see cref="IndexStatus.Failed"/> for the life of the process, a search refuses for as long,
	/// and the fix is to restart an application that would have recovered by itself a minute later.
	/// </para>
	///
	/// <para>
	/// The probe is not repeated once it has passed. An attempt can fail on either side of it, and
	/// re-asking a backend that already answered costs a round trip for an answer that will not have
	/// changed — where the part that failed after it is the part worth trying again.
	/// </para>
	/// </summary>
	private async Task RunFirstPassUntilItSucceedsAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			await this.AttemptAsync(stoppingToken).ConfigureAwait(false);

			if (this._state.Status != IndexStatus.Failed || this._failedRetryInterval <= TimeSpan.Zero)
			{
				return;
			}

			try
			{
				await Task.Delay(this._failedRetryInterval, stoppingToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				// Shutting down. Waiting out the rest of the interval first would make stopping take
				// as long as the retry schedule, which is a setting about recovery and not about how
				// long a host may take to stop.
				return;
			}
		}
	}

	/// <summary>
	/// One attempt at bringing the index to <see cref="IndexStatus.Ready"/>: the probe, the pass, and
	/// the start of the front end. All three have to succeed.
	/// </summary>
	private async Task AttemptAsync(CancellationToken cancellationToken)
	{
		try
		{
			if (!this._probePassed && this._healthCheck is not null)
			{
				await this._healthCheck.CheckAsync(cancellationToken).ConfigureAwait(false);

				this._probePassed = true;
			}

			// The pipeline is synchronous and both CPU- and IO-bound. Handing it to the thread pool is
			// what keeps it off the thread the host is starting on.
			IndexingResult result = await Task.Run(this._runIndex, cancellationToken).ConfigureAwait(false);

			Console.WriteLine(
				$"Initial index: recorded={result.ChangesRecorded}, scanned={result.FilesScanned}, " +
				$"indexed={result.FilesIndexed}, unchanged={result.FilesUnchanged}, deferred={result.FilesDeferred}, " +
				$"chunks={result.ChunksIndexed}, vectors={result.VectorsIndexed}");

			// After the pass and before the index is declared ready. A front end that could not start
			// fails the attempt, and the attempt is tried again — where declaring the index ready first
			// would leave one that answers and has quietly stopped following the folder. It compares the
			// folder against the index once itself before its loops run, which after the pass is cheap:
			// every file it finds is already recorded.
			if (this._indexer is not null && !this._indexer.IsRunning)
			{
				await this._indexer.StartAsync(cancellationToken).ConfigureAwait(false);
			}

			this._state.MarkReady(DateTime.UtcNow);
		}
		catch (OperationCanceledException)
		{
			// The host is shutting down. Not an indexing failure, and marking it as one would leave a
			// stopped process reporting a broken index.
			throw;
		}
		catch (Exception ex)
		{
			// Nothing else observes this attempt. A background failure that only writes to a log leaves
			// retrieval answering as though the index were merely empty, so the state carries it.
			this._state.MarkFailed(ex);

			Console.WriteLine($"Initial index failed: {ex.Message}");
		}
	}
}
