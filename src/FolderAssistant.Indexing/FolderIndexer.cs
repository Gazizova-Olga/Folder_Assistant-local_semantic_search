using FolderAssistant.Indexing.Outbox;
using FolderAssistant.Indexing.Pipeline;
using FolderAssistant.Indexing.Scanning;
using FolderAssistant.Indexing.Watching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderAssistant.Indexing;

/// <summary>
/// Owns the front end's loops and runs them together: the watcher, the per-change pipeline, the
/// periodic reconciler and the outbox dispatcher.
///
/// <para>
/// Each of those is correct on its own and useless on its own — the watcher publishes to a channel
/// nothing reads, the dispatcher drains a queue nothing fills. This is the one place that knows all
/// four, so it is also the one place the things they share are decided: one filter, so every walker
/// agrees on what the corpus is; one hold, so a reconciliation pass defers for the same batch the
/// watcher is holding; and one order of stopping, so an edit made just before shutdown is recorded
/// rather than lost.
/// </para>
///
/// <para>
/// <strong>Starting compares the folder against the index before anything else runs.</strong> The
/// watcher sees only what happens after it attaches, and a folder edited while nothing was running
/// has changes nothing will ever report. One pass at start is what makes those temporary. The loops
/// start after it, so the outbox already holds that pass's deliveries when the dispatcher first looks.
/// </para>
///
/// <para>
/// <strong>Stopping runs in the opposite order, and the order is the guarantee.</strong> The watcher
/// goes first and hands over whatever was still inside its quiet window; the pipeline then finishes
/// recording those changes into the store, which is durable; only then are the loops that stop on
/// cancellation told to. Cancelling everything at once would drop the settled change the pipeline was
/// about to record, and the file would stay stale until a reconciliation happened to notice.
/// </para>
/// </summary>
public sealed class FolderIndexer : IFolderIndexer
{
    private readonly FolderIndexerOptions _options;
    private readonly IIndexStore _index;
    private readonly IContentHasher _hasher;
    private readonly IndexablePathFilter _filter;
    private readonly ChangePipeline _pipeline;
    private readonly OutboxDispatcher _dispatcher;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;

    /// <summary>Start and stop take turns; a second start while one is running is refused rather than raced.</summary>
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private volatile Running? _running;

    /// <param name="index">Where file records live. The application supplies it; see <see cref="IIndexStore"/>.</param>
    /// <param name="outbox">Where deliveries queue. Usually the same object as <paramref name="index"/>, and must be the same database.</param>
    /// <param name="vectorizer">The application's side of the seam each delivery crosses.</param>
    /// <param name="hasher">Change detection. It must hash what the application's own writers hash; see <see cref="Sha256ContentHasher"/>.</param>
    /// <param name="loggerFactory">
    /// Optional. Every loop here survives its faults and keeps converging, which is exactly what makes
    /// a loop failing every pass look like one with nothing to do; a factory is how those survivals
    /// get recorded. Omitted, the whole front end runs silent.
    /// </param>
    public FolderIndexer(
        FolderIndexerOptions options,
        IIndexStore index,
        IOutboxStore outbox,
        IVectorizationService vectorizer,
        IContentHasher hasher,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(vectorizer);
        ArgumentNullException.ThrowIfNull(hasher);

        _options = options;
        _index = index;
        _hasher = hasher;
        _loggers = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggers.CreateLogger<FolderIndexer>();

        _filter = new IndexablePathFilter(options.MetadataFolderName, options.IndexableExtensions, options.MaxContentBytes);

        _pipeline = new ChangePipeline(
            options.RootPath,
            _filter,
            index,
            hasher,
            options.SettleProbeInterval,
            options.RetryDelay,
            options.MaxAttempts,
            _loggers.CreateLogger<ChangePipeline>());

        _dispatcher = new OutboxDispatcher(
            options.RootPath,
            outbox,
            vectorizer,
            options.Dispatcher,
            logger: _loggers.CreateLogger<OutboxDispatcher>());
    }

    /// <inheritdoc/>
    public bool IsRunning => _running is not null;

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_running is not null)
            {
                throw new InvalidOperationException("The indexer is already running.");
            }

            // The watcher exists before the first pass so the pass can ask it about holds. Nothing
            // has started it, so it observes nothing yet.
            FileSystemWatcherHost watcher = new(_options.RootPath, _filter, _options.QuietWindow, _options.MaxHoldDuration);

            Reconciler reconciler = new(
                _options.RootPath,
                _filter,
                _index,
                _hasher,
                _options.HashingParallelism,
                hold: watcher,
                _loggers.CreateLogger<Reconciler>());

            // Whatever changed while nothing was watching. Deliberately not wrapped: a first pass that
            // cannot read the folder at all is the caller's to hear about, where the periodic one
            // survives a bad pass because it has a next one.
            ReconcileResult startup = await reconciler.ReconcileAsync(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Reconciled {Root} at start: {Examined} files examined, {Skipped} skipped, {Changes} changes recorded.",
                _options.RootPath,
                startup.Examined,
                startup.Skipped,
                startup.Changes.Count);

            CancellationTokenSource stopping = new();
            watcher.Start();

            Task pipeline = Task.Run(() => _pipeline.RunAsync(watcher.Changes, stopping.Token), CancellationToken.None);
            Task dispatcher = Task.Run(() => _dispatcher.RunAsync(stopping.Token), CancellationToken.None);

            Task? periodic = _options.ReconciliationInterval > TimeSpan.Zero
                ? Task.Run(() => reconciler.RunPeriodicallyAsync(_options.ReconciliationInterval, stopping.Token), CancellationToken.None)
                : null;

            _running = new Running(watcher, stopping, pipeline, dispatcher, periodic);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            Running? running = _running;

            if (running is null)
            {
                return;
            }

            // 1. Stop observing. Disposing the watcher publishes whatever was still inside its quiet
            //    window and completes the channel, so the pipeline sees every edit made before this
            //    point and then sees the source end.
            await running.Watcher.DisposeAsync().ConfigureAwait(false);

            // 2. Let the pipeline finish recording. It completes by itself once its source has, and
            //    every change it took — including one waiting to be tried again — has been written or
            //    given up on. Bounded by the caller's token, not cut short by this type.
            await WaitForDrainAsync(running.Pipeline, cancellationToken).ConfigureAwait(false);

            // 3. The loops that only stop when told to. Anything the dispatcher had claimed and not
            //    resolved is returned to the queue at the next start.
            await running.Stopping.CancelAsync().ConfigureAwait(false);

            await AwaitQuietlyAsync(running.Pipeline).ConfigureAwait(false);
            await AwaitQuietlyAsync(running.Dispatcher).ConfigureAwait(false);
            await AwaitQuietlyAsync(running.Periodic).ConfigureAwait(false);

            running.Stopping.Dispose();
            _running = null;

            _logger.LogInformation("Stopped indexing {Root}.", _options.RootPath);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <inheritdoc/>
    public ValueTask NotifyCreatedAsync(string absolutePath, CancellationToken cancellationToken = default)
        => _running?.Watcher.NotifyCreatedAsync(absolutePath, cancellationToken) ?? ValueTask.CompletedTask;

    /// <inheritdoc/>
    public ValueTask NotifyChangedAsync(string absolutePath, CancellationToken cancellationToken = default)
        => _running?.Watcher.NotifyChangedAsync(absolutePath, cancellationToken) ?? ValueTask.CompletedTask;

    /// <inheritdoc/>
    public ValueTask NotifyDeletedAsync(string absolutePath, CancellationToken cancellationToken = default)
        => _running?.Watcher.NotifyDeletedAsync(absolutePath, cancellationToken) ?? ValueTask.CompletedTask;

    /// <inheritdoc/>
    /// <remarks>
    /// While nothing is running there is nothing to hold back, so the handle holds nothing — for the
    /// same reason a report made then is dropped: a change made while nothing was watching is what
    /// the pass at the next start is for.
    /// </remarks>
    public IDisposable BeginBatch()
        => _running?.Watcher.BeginBatch() ?? NoHold.Instance;

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);

        _lifecycle.Dispose();
    }

    /// <summary>
    /// Waits for the pipeline to finish on its own, unless the caller gives up first — in which case
    /// what it had not recorded is lost to this run, and said so.
    /// </summary>
    private async Task WaitForDrainAsync(Task pipeline, CancellationToken cancellationToken)
    {
        TaskCompletionSource gaveUp = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using CancellationTokenRegistration registration = cancellationToken.Register(() => gaveUp.TrySetResult());

        Task first = await Task.WhenAny(pipeline, gaveUp.Task).ConfigureAwait(false);

        if (first != pipeline)
        {
            _logger.LogWarning(
                "Stopped before every observed change was recorded; what is left is the next start's reconciliation to find.");
        }
    }

    private async Task AwaitQuietlyAsync(Task? loop)
    {
        if (loop is null)
        {
            return;
        }

        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping is what cancelled it.
        }
        catch (Exception ex)
        {
            // Every loop catches its own faults and continues, so this is a loop that ended for a
            // reason nobody planned for. Recorded here because a faulted task nobody awaits is silent,
            // and stopping continues because one loop's fault must not stop the others from stopping.
            _logger.LogWarning(ex, "A background indexing loop faulted; stopping continues.");
        }
    }

    /// <summary>What one run owns, so that stopping can take it apart in order.</summary>
    private sealed record Running(
        FileSystemWatcherHost Watcher,
        CancellationTokenSource Stopping,
        Task Pipeline,
        Task Dispatcher,
        Task? Periodic);

    private sealed class NoHold : IDisposable
    {
        public static readonly NoHold Instance = new();

        public void Dispose()
        {
            // Nothing was held.
        }
    }
}
