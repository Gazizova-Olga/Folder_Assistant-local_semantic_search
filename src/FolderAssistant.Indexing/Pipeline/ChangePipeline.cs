using System.Threading.Channels;
using FolderAssistant.Indexing.Scanning;
using FolderAssistant.Indexing.Watching;

namespace FolderAssistant.Indexing.Pipeline;

/// <summary>
/// Turns settled changes into index updates, one file at a time.
///
/// <para>
/// The event is a hint about <em>which</em> file to look at, never a statement about what happened to
/// it. By the time a change is processed the file may have been recreated, deleted again, or saved
/// with identical bytes, and the watcher's own report of a rename is two events for two paths. So
/// every change is decided by what is on disk now against what the index recorded: the kind is not
/// read at all.
/// </para>
///
/// <para>
/// <strong>A concurrent write may delay indexing a file, but must never stop it.</strong> This path
/// reads files that other processes are writing, and a share-<c>Read</c> open that loses to a live
/// writer is the ordinary case rather than an error. A change that meets one is tried again later,
/// not dropped.
/// </para>
/// </summary>
public sealed class ChangePipeline
{
    /// <summary>
    /// How many times one change is tried before it is left to the reconciler. Bounded because an
    /// editor can hold a file open for hours, and the periodic reconcile already retries every file it
    /// could not read on every pass — chasing the same file here as well would only hold a retry open
    /// for as long as the writer holds its handle.
    /// </summary>
    public const int DefaultMaxAttempts = 5;

    private readonly string _rootPath;
    private readonly IIndexStore _store;
    private readonly IContentHasher _hasher;
    private readonly IFileSettler _settler;
    private readonly TimeSpan _retryDelay;
    private readonly int _maxAttempts;

    /// <param name="rootPath">The watched root. Keys are derived from it exactly as the reconciler derives them.</param>
    /// <param name="settleProbeInterval">How long a file's size and write time must hold still before it is hashed.</param>
    /// <param name="retryDelay">How long a change that met a busy file waits before it is tried again.</param>
    /// <param name="maxAttempts">Total tries for one change, including the first.</param>
    public ChangePipeline(
        string rootPath,
        IIndexStore store,
        IContentHasher hasher,
        TimeSpan settleProbeInterval,
        TimeSpan retryDelay,
        int maxAttempts = DefaultMaxAttempts)
        : this(rootPath, store, hasher, new FileSettler(settleProbeInterval), retryDelay, maxAttempts)
    {
    }

    internal ChangePipeline(
        string rootPath,
        IIndexStore store,
        IContentHasher hasher,
        IFileSettler settler,
        TimeSpan retryDelay,
        int maxAttempts = DefaultMaxAttempts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(settler);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        _rootPath = Path.GetFullPath(rootPath);
        _store = store;
        _hasher = hasher;
        _settler = settler;
        _retryDelay = retryDelay;
        _maxAttempts = maxAttempts;
    }

    /// <summary>
    /// Processes <paramref name="changes"/> until the source completes and every change taken from it
    /// has finished — including those waiting to be tried again — or until cancelled.
    /// </summary>
    public async Task RunAsync(ChannelReader<ObservedChange> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);

        Channel<PendingChange> work = Channel.CreateUnbounded<PendingChange>(
            new UnboundedChannelOptions { SingleReader = true });

        // A change is outstanding from the moment it is taken from the source until it is finished,
        // and it stays outstanding while it waits out a retry delay. Completing the work channel as
        // soon as the source completed would discard exactly those waiting changes — the ones that
        // met a busy file — which is the failure this type exists to prevent.
        int outstanding = 0;
        int sourceCompleted = 0;

        void CompleteIfIdle()
        {
            if (Volatile.Read(ref sourceCompleted) == 1 && Volatile.Read(ref outstanding) == 0)
            {
                work.Writer.TryComplete();
            }
        }

        async Task PumpAsync()
        {
            try
            {
                await foreach (ObservedChange change in changes.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    Interlocked.Increment(ref outstanding);
                    await work.Writer.WriteAsync(new PendingChange(change, Attempt: 1), cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                // Also when the source faults, so that what was already taken still finishes and the
                // fault is then rethrown below instead of leaving the loop waiting on a channel that
                // will never complete.
                Interlocked.Exchange(ref sourceCompleted, 1);
                CompleteIfIdle();
            }
        }

        async Task RetryLaterAsync(PendingChange pending)
        {
            try
            {
                await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
                work.Writer.TryWrite(pending with { Attempt = pending.Attempt + 1 });
            }
            catch (OperationCanceledException)
            {
                // Stopping.
            }
        }

        Task pump = PumpAsync();

        try
        {
            await foreach (PendingChange pending in work.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                bool retry;

                try
                {
                    retry = await ProcessOnceAsync(pending.Change, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // Deliberately everything that is not a busy file. One change failing — the store
                    // refusing a write, say — must not end processing for every change behind it; this
                    // one is dropped, and the periodic reconcile restates the file from disk.
                    retry = false;
                }

                if (retry && pending.Attempt < _maxAttempts)
                {
                    _ = RetryLaterAsync(pending);
                }
                else
                {
                    Interlocked.Decrement(ref outstanding);
                    CompleteIfIdle();
                }
            }

            await pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    /// <summary>
    /// Tries one change once. Returns <see langword="true"/> when the file was busy and the change
    /// should be tried again; <see langword="false"/> when it is finished, whether or not anything was
    /// written.
    /// </summary>
    internal async Task<bool> ProcessOnceAsync(ObservedChange change, CancellationToken cancellationToken)
    {
        string relativePath = IndexKey.For(_rootPath, change.Path);

        SettleResult settled = await _settler.SettleAsync(change.Path, cancellationToken).ConfigureAwait(false);

        if (settled == SettleResult.Busy)
        {
            return true;
        }

        // No file at this path — deleted, or never a file. A folder created, moved in or deleted also
        // lands here: it arrives as one event for the folder rather than one per file inside it, so
        // there is nothing to hash, and the files beneath it are the periodic reconcile's to find.
        if (settled == SettleResult.Missing)
        {
            await RemoveAsync(relativePath, cancellationToken).ConfigureAwait(false);
            return false;
        }

        FileRecord current;

        // The settle probe and the hash are separate opens, so a writer can take the file in between.
        // That is a file which is not settled after all, not a failed change: try again. Letting the
        // exception reach the catch-all would drop the change with nothing to retry it, and the file
        // would stay stale until a reconcile happened to pass over it.
        try
        {
            string hash = await _hasher.HashAsync(change.Path, cancellationToken).ConfigureAwait(false);
            FileInfo info = new(change.Path);
            current = new FileRecord(relativePath, hash, info.Length, FileTimestamps.ReadCreatedUtc(info));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }

        FileRecord? recorded = await _store.ReadAsync(relativePath, cancellationToken).ConfigureAwait(false);

        if (recorded is null)
        {
            await ApplyAsync(new ReconciledChange(relativePath, FileDelta.Added, current), cancellationToken).ConfigureAwait(false);
        }
        else if (!string.Equals(recorded.ContentHash, current.ContentHash, StringComparison.Ordinal))
        {
            await ApplyAsync(
                new ReconciledChange(relativePath, FileDelta.Modified, FileTimestamps.KeepRecordedCreation(current, recorded)),
                cancellationToken).ConfigureAwait(false);
        }

        // Identical content — a touch, or a save that changed nothing — costs no write.
        return false;
    }

    private async Task RemoveAsync(string relativePath, CancellationToken cancellationToken)
    {
        FileRecord? recorded = await _store.ReadAsync(relativePath, cancellationToken).ConfigureAwait(false);

        if (recorded is not null)
        {
            await ApplyAsync(new ReconciledChange(relativePath, FileDelta.Removed, recorded), cancellationToken).ConfigureAwait(false);
        }
    }

    private Task ApplyAsync(ReconciledChange change, CancellationToken cancellationToken)
        => _store.ApplyAsync([change], cancellationToken);

    private readonly record struct PendingChange(ObservedChange Change, int Attempt);
}
