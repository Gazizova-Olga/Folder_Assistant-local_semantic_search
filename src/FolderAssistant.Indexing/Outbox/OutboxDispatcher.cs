using FolderAssistant.Indexing.Scanning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderAssistant.Indexing.Outbox;

/// <summary>Tuning for <see cref="OutboxDispatcher"/>.</summary>
public sealed record OutboxDispatcherOptions
{
    /// <summary>
    /// Total attempts at one delivery before it is abandoned. Bounded because a delivery that fails for a
    /// reason that will not clear — a file the embedding side cannot read, a model that is not installed —
    /// would otherwise be retried for as long as the process lives.
    /// </summary>
    public int MaxAttempts { get; init; } = 10;

    /// <summary>Delay before the second attempt; each later one doubles it, up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan BaseRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest any single retry waits.</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many files are delivered at once. Defaults to one: each delivery is a round trip into a single
    /// backend, and what that backend can take in parallel is a property of the backend, not of this machine.
    /// </summary>
    public int Parallelism { get; init; } = 1;

    /// <summary>How many due operations one drain claims.</summary>
    public int BatchSize { get; init; } = 32;

    /// <summary>How long an empty outbox is left before it is looked at again.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long the record of a succeeded delivery is kept before a quiet moment discards it. Failed
    /// operations are never discarded — they are the record that a file is not in the index — so this bounds
    /// only the part of the queue that says work arrived.
    ///
    /// <para>
    /// A day, because the window is for a person: long enough that whoever is looking into what a burst did
    /// still has the rows, short enough that a folder edited for years does not carry one row per edit for
    /// all of them. <see cref="TimeSpan.Zero"/> turns pruning off and keeps everything.
    /// </para>
    /// </summary>
    public TimeSpan DeliveredRetention { get; init; } = TimeSpan.FromDays(1);
}

/// <summary>
/// Delivers queued changes to the embedding side, at least once, one file at a time per file.
///
/// <para>
/// Four rules make the log trustworthy, and each is tested by breaking it.
/// </para>
///
/// <para>
/// <strong>Operations for one file are delivered in the order they were queued, never concurrently.</strong>
/// A batch is split by file: parallel across files, sequential within one. An upsert and a delete for the
/// same file, run together or out of order, can leave vectors behind for a file that no longer exists.
/// </para>
///
/// <para>
/// <strong>Marking a file delivered is conditional on the content that was delivered.</strong> See
/// <see cref="IOutboxStore.TryMarkSyncedAsync"/>.
/// </para>
///
/// <para>
/// <strong>A removal is delivered only while the file is still gone.</strong> A file that came back before
/// its queued removal was delivered is recorded again, with the upsert that re-embeds it queued behind the
/// removal. Delivered anyway, the removal would clear the file's rows out from under that upsert, which
/// would find nothing recorded and skip — leaving the file absent until a periodic pass rediscovered it.
/// </para>
///
/// <para>
/// <strong>A delivery that will not be tried again is retired as failed, with the error that ended it.</strong>
/// Retired as done, an outbox full of abandoned work would look exactly like one where everything had
/// arrived, and the file's only symptom would be a search that quietly does not find it. And it is the
/// <em>last</em> error that is kept: a run of "connection refused" followed by "model not found" is only
/// actionable if the second survives.
/// </para>
/// </summary>
public sealed class OutboxDispatcher
{
    private readonly string _rootPath;
    private readonly IOutboxStore _store;
    private readonly IVectorizationService _vectorizer;
    private readonly OutboxDispatcherOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public OutboxDispatcher(
        string rootPath,
        IOutboxStore store,
        IVectorizationService vectorizer,
        OutboxDispatcherOptions? options = null,
        TimeProvider? time = null,
        ILogger<OutboxDispatcher>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(vectorizer);

        _rootPath = Path.GetFullPath(rootPath);
        _store = store;
        _vectorizer = vectorizer;
        _options = options ?? new OutboxDispatcherOptions();
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<OutboxDispatcher>.Instance;

        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.Parallelism, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.BatchSize, 1);
    }

    /// <summary>
    /// Claims one batch of due operations and delivers it. Returns how many were claimed; zero means the
    /// outbox had nothing due.
    /// </summary>
    public async Task<int> DrainOnceAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<OutboxOp> claimed = await _store
            .ClaimDueAsync(_options.BatchSize, _time.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);

        if (claimed.Count == 0)
        {
            return 0;
        }

        // Grouping keeps claim order within each file, and a file's operations run one after another inside
        // a single body — so the parallelism below is across files only.
        await Parallel.ForEachAsync(
            claimed.GroupBy(op => op.RelativePath, StringComparer.Ordinal),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _options.Parallelism,
                CancellationToken = cancellationToken,
            },
            async (fileOps, token) =>
            {
                foreach (OutboxOp op in fileOps)
                {
                    await DeliverAsync(op, token).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);

        return claimed.Count;
    }

    /// <summary>
    /// Drains the outbox until cancelled: straight on while there is work, polling while there is none.
    ///
    /// <para>
    /// <strong>A failed drain must not end the loop</strong>, for the reconciler's reason: every queued file
    /// depends on it, and a loop that stopped would look exactly like an outbox with nothing in it. A drain
    /// that failed part-way can leave operations claimed and never resolved, so the next pass returns them to
    /// the queue first.
    /// </para>
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // A previous process may have stopped mid-delivery.
        bool requeueInFlight = true;
        bool deliveredSinceQuiet = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int claimed = 0;
                bool drained = false;

                try
                {
                    if (requeueInFlight)
                    {
                        await _store.RequeueInFlightAsync(cancellationToken).ConfigureAwait(false);
                        requeueInFlight = false;
                    }

                    claimed = await DrainOnceAsync(cancellationToken).ConfigureAwait(false);
                    drained = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Deliberately everything: the store refusing a claim, or failing to record an outcome.
                    //
                    // Warning, for the reconcile loop's reason: every queued file depends on this loop
                    // continuing, and a drain that fails on every pass is indistinguishable from an
                    // outbox with nothing in it.
                    _logger.LogWarning(ex, "A drain of the outbox failed; the loop continues and the next pass retries.");
                    requeueInFlight = true;
                }

                if (claimed > 0)
                {
                    deliveredSinceQuiet = true;
                }
                else if (drained && deliveredSinceQuiet)
                {
                    // The burst just ended. This is the one moment a checkpoint is cheap and worth it: nothing
                    // is delivering, and whatever the burst wrote is all still waiting to be reclaimed. Per
                    // operation it would block readers again and again for the same space; on every idle poll
                    // it would run forever against a folder nobody is touching.
                    deliveredSinceQuiet = false;

                    // Prune before the checkpoint, so the rows this drops are space the checkpoint then
                    // reclaims rather than space the next burst has to wait for.
                    await PruneQuietlyAsync(cancellationToken).ConfigureAwait(false);
                    await CheckpointQuietlyAsync(cancellationToken).ConfigureAwait(false);
                }

                if (claimed == 0)
                {
                    await Task.Delay(_options.PollInterval, _time, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping. Anything claimed and not resolved is requeued on the next start.
        }
    }

    /// <summary>
    /// Dropping the record of deliveries that succeeded, at the one moment it is free: nothing is delivering,
    /// and the rows a burst just retired are the ones worth dropping. Off when the retention window is zero.
    /// Like the checkpoint, it delivers nothing, so a store that cannot prune must not end the loop.
    /// </summary>
    private async Task PruneQuietlyAsync(CancellationToken cancellationToken)
    {
        if (_options.DeliveredRetention <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            int pruned = await _store.PruneDeliveredAsync(_options.DeliveredRetention, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            if (pruned > 0)
            {
                _logger.LogDebug("Pruned {Pruned} delivered operations from the outbox.", pruned);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Costs disk until the next quiet moment and nothing else: every row it would have dropped is one
            // whose work already arrived. Debug for the checkpoint's reason — a queue quietly growing for the
            // life of a folder has no other symptom.
            _logger.LogDebug(ex, "Could not prune delivered operations from the outbox; delivery is unaffected.");
        }
    }

    /// <summary>
    /// A checkpoint reclaims disk; it delivers nothing. One the store cannot take right now must not end the
    /// loop every queued file depends on.
    /// </summary>
    private async Task CheckpointQuietlyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _store.CheckpointAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Costs disk space until the next burst ends; changes nothing that is delivered or retrievable —
            // so debug, and recorded rather than swallowed only because a log that never fills is the one
            // symptom a folder quietly growing a write-ahead log would otherwise have.
            _logger.LogDebug(ex, "Could not checkpoint the write-ahead log after a burst; delivery is unaffected.");
        }
    }

    private async Task DeliverAsync(OutboxOp op, CancellationToken cancellationToken)
    {
        try
        {
            if (op.Kind == DeliveryKind.Delete)
            {
                await DeleteAsync(op, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await UpsertAsync(op, cancellationToken).ConfigureAwait(false);
            }

            await _store.MarkDoneAsync(op.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await RecordFailureAsync(op, ex, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DeleteAsync(OutboxOp op, CancellationToken cancellationToken)
    {
        // Recorded again since the removal was queued: the file came back, and the store queued the upsert
        // that re-embeds it behind this operation. The removal has been overtaken and delivers nothing. A
        // return landing between this read and the embedding side ending the row is not covered here; that
        // window is one store round-trip, not an embed.
        DeliveryRecord? present = await _store.ReadForDeliveryAsync(op.RelativePath, cancellationToken).ConfigureAwait(false);

        if (present is not null)
        {
            return;
        }

        await _vectorizer.DeleteAsync(FileIdentity.For(op.RelativePath), cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertAsync(OutboxOp op, CancellationToken cancellationToken)
    {
        DeliveryRecord? delivery = await _store.ReadForDeliveryAsync(op.RelativePath, cancellationToken).ConfigureAwait(false);

        // Gone since it was queued; its removal is queued separately.
        if (delivery is null)
        {
            return;
        }

        FileRecord file = delivery.File;

        // Already delivered for exactly this content — a redelivery after a crash, or a change that turned out
        // to be a touch. At-least-once delivery is only affordable because repeating one costs nothing.
        if (string.Equals(delivery.LastSyncedHash, file.ContentHash, StringComparison.Ordinal))
        {
            return;
        }

        string absolutePath = Path.Combine(_rootPath, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        FileMetadata metadata = new(absolutePath, file.Size, file.CreatedUtc, Path.GetExtension(absolutePath), file.ContentHash);

        // Share-Read, like every other read of the folder: a live writer denies it, and that failure is an
        // attempt to be retried rather than a torn copy delivered.
        await using (FileStream content = new(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await _vectorizer.UpsertAsync(FileIdentity.For(file.RelativePath), content, metadata, cancellationToken)
                .ConfigureAwait(false);
        }

        // Under the hash read before delivery, never the record written back whole. If the file was rewritten
        // meanwhile this writes nothing, and the delivery already queued for the new content does the work.
        await _store.TryMarkSyncedAsync(file.RelativePath, file.ContentHash, cancellationToken).ConfigureAwait(false);
    }

    private Task RecordFailureAsync(OutboxOp op, Exception ex, CancellationToken cancellationToken)
    {
        int attempts = op.Attempts + 1;
        string error = $"{ex.GetType().Name}: {ex.Message}";

        if (attempts >= _options.MaxAttempts)
        {
            // The only error this library raises, and the level is the point: the operation is retired and
            // the file marked failed, so the state is durable, correct and completely silent. Nothing will
            // try it again, and its one other symptom is a search that does not find a file that is there.
            _logger.LogError(
                ex,
                "Giving up on the {Kind} of {Path} after {Attempts} attempts; it is retired as failed and will not be tried again.",
                op.Kind,
                op.RelativePath,
                attempts);

            return _store.MarkAbandonedAsync(op.Id, attempts, error, cancellationToken);
        }

        TimeSpan retryDelay = RetryDelay(attempts);

        // Debug: a delivery that will be tried again is the ordinary cost of reaching another process, and
        // at any higher level an embedding backend restarting would page somebody about work that recovers
        // on its own.
        _logger.LogDebug(
            ex,
            "The {Kind} of {Path} failed (attempt {Attempts}); trying again in {RetryDelay}.",
            op.Kind,
            op.RelativePath,
            attempts,
            retryDelay);

        return _store.RescheduleAsync(op.Id, attempts, _time.GetUtcNow() + retryDelay, error, cancellationToken);
    }

    /// <summary>Doubles from <see cref="OutboxDispatcherOptions.BaseRetryDelay"/>, capped.</summary>
    internal TimeSpan RetryDelay(int failedAttempts)
    {
        double factor = Math.Pow(2, Math.Min(failedAttempts - 1, 30));
        double ticks = Math.Min(_options.BaseRetryDelay.Ticks * factor, _options.MaxRetryDelay.Ticks);

        return TimeSpan.FromTicks((long)ticks);
    }
}
