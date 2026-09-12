using System.Collections.Concurrent;
using FolderAssistant.Indexing.Watching;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderAssistant.Indexing.Scanning;

/// <summary>
/// Walks the folder and reconciles what is on disk against what the index has recorded.
///
/// <para>
/// This is the safety net, not the main path. Watching delivers changes promptly and imperfectly:
/// the operating system drops events when its buffer overflows — during exactly the bursts an index
/// most needs to keep up with — and a save implemented as write-temp-then-rename can arrive as a
/// shape the watcher reports but nothing downstream recognises. Every such miss is silent. A
/// periodic full comparison is what makes them temporary rather than permanent.
/// </para>
///
/// <para>
/// Because it is the safety net, its own failures matter more than an ordinary component's. A
/// reconciler that stops running looks exactly like one with nothing to do, and the symptom appears
/// much later as a search that quietly does not find a file. Two rules follow, and both are
/// mutation-tested rather than asserted in a comment.
/// </para>
/// </summary>
public sealed class Reconciler
{
    private readonly string _rootPath;
    private readonly IIndexStore _store;
    private readonly IContentHasher _hasher;
    private readonly IndexablePathFilter _filter;
    private readonly int _maxDegreeOfParallelism;
    private readonly IBatchHoldState? _hold;

    /// <summary>How often a deferred pass re-asks. Short: the wait ends when the caller says so.</summary>
    private static readonly TimeSpan HoldPollInterval = TimeSpan.FromMilliseconds(50);
    private readonly ILogger _logger;

    /// <param name="filter">
    /// What belongs in the corpus. The same instance the watcher and the per-change path use, so
    /// that a pass here and an event there cannot disagree about a file.
    /// </param>
    /// <param name="maxDegreeOfParallelism">
    /// How many files are hashed at once. Sizes a disk- and CPU-bound job, so it scales with the
    /// machine — unrelated to how many files are delivered onward at once, which is one network
    /// round-trip each into a single backend. One knob for both could only ever suit one of them.
    /// </param>
    /// <param name="hold">
    /// Optional. Given one, a scheduled pass waits while a batch is held. Omitted, passes run on
    /// their interval regardless — which is right where nothing can hold, and wrong the moment
    /// something can.
    /// </param>
    /// <param name="logger">
    /// Optional. Omitted, the reconciler runs silent: it records what it survives, but takes the
    /// logger to record it with from whoever hosts it rather than choosing one.
    /// </param>
    public Reconciler(
        string rootPath,
        IndexablePathFilter filter,
        IIndexStore store,
        IContentHasher hasher,
        int maxDegreeOfParallelism = 0,
        IBatchHoldState? hold = null,
        ILogger<Reconciler>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(hasher);

        _rootPath = Path.GetFullPath(rootPath);
        _store = store;
        _hasher = hasher;
        _filter = filter;
        _maxDegreeOfParallelism = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount;
        _hold = hold;
        _logger = logger ?? NullLogger<Reconciler>.Instance;
    }

    /// <summary>
    /// Compares the folder against the index once, and applies what it finds.
    /// </summary>
    public async Task<ReconcileResult> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, FileRecord> recorded = await _store.ReadAllAsync(cancellationToken)
            .ConfigureAwait(false);

        ConcurrentDictionary<string, FileRecord> onDisk = new(StringComparer.Ordinal);
        ConcurrentBag<string> skipped = [];

        await Parallel.ForEachAsync(
            EnumerateIndexableFiles(),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = _maxDegreeOfParallelism,
            },
            async (absolutePath, token) =>
            {
                string relativePath = IndexKey.For(_rootPath, absolutePath);

                try
                {
                    FileInfo info = new(absolutePath);
                    long size = info.Length;

                    // Over the size bound, so not part of the corpus. It is left out the way an
                    // unreported path is, not the way an unreadable file is: it is absent from the
                    // picture and not in `skipped`, so a record the index holds for it is removed
                    // below. A file that grows past the bound leaves the index, and one that shrinks
                    // back is added again — and neither costs a hash of the whole thing first.
                    if (!_filter.ShouldIndex(absolutePath, size))
                    {
                        return;
                    }

                    string hash = await _hasher.HashAsync(absolutePath, token).ConfigureAwait(false);

                    onDisk[relativePath] = new FileRecord(
                        relativePath, hash, size, FileTimestamps.ReadCreatedUtc(info));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A file being written, locked, or unreadable. This is ordinary: the indexer does
                    // not own the folder it indexes, and an editor holding a file is the normal case
                    // rather than an error condition.
                    //
                    // The file is left out of this pass's picture ENTIRELY — not recorded with an
                    // empty or placeholder hash. A blank hash becomes the file's stored identity, and
                    // every other unhashable file then carries the same one, so any comparison keyed
                    // on content sees a folder full of identical files.
                    //
                    // And the exception is swallowed rather than allowed to leave this delegate:
                    // Parallel.ForEachAsync cancels its remaining work when a body throws, so one
                    // locked file would end the pass for every file after it.
                    skipped.Add(relativePath);

                    // Debug, not warning. This is the expected consequence of indexing a folder
                    // somebody else is using, and at warning level an ordinary editing session would
                    // fill the log with it. A file that stays locked repeats the same line every pass.
                    _logger.LogDebug(ex, "Skipped hashing {Path} this pass; it is locked or unreadable.", relativePath);
                }
            }).ConfigureAwait(false);

        List<ReconciledChange> changes = [];

        foreach ((string relativePath, FileRecord current) in onDisk)
        {
            if (!recorded.TryGetValue(relativePath, out FileRecord? previous))
            {
                changes.Add(new ReconciledChange(relativePath, FileDelta.Added, current));
            }
            else if (!string.Equals(previous.ContentHash, current.ContentHash, StringComparison.Ordinal))
            {
                changes.Add(new ReconciledChange(
                    relativePath, FileDelta.Modified, FileTimestamps.KeepRecordedCreation(current, previous)));
            }
        }

        foreach ((string relativePath, FileRecord previous) in recorded)
        {
            // A file that was skipped is NOT a file that was removed. It is on disk and simply could
            // not be read this pass, so treating its absence from `onDisk` as a deletion would drop
            // its index entry and re-add it on the next pass — an edit-and-delete cycle driven purely
            // by someone else holding the file open.
            if (!onDisk.ContainsKey(relativePath) && !skipped.Contains(relativePath))
            {
                changes.Add(new ReconciledChange(relativePath, FileDelta.Removed, previous));
            }
        }

        if (changes.Count > 0)
        {
            await _store.ApplyAsync(changes, cancellationToken).ConfigureAwait(false);
        }

        return new ReconcileResult(onDisk.Count, skipped.Count, changes);
    }

    /// <summary>
    /// Reconciles on a fixed interval until cancelled.
    ///
    /// <para>
    /// <strong>A failed pass must not end the loop.</strong> This loop is what heals every change the
    /// watcher dropped; if one transient fault could stop it, the index would silently stop
    /// converging for the rest of the process's life and nothing would report it — the folder would
    /// simply drift, and the first symptom would be a search that does not find a file that is
    /// plainly there.
    /// </para>
    /// </summary>
    public async Task RunPeriodicallyAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await WaitForHoldToClearAsync(cancellationToken).ConfigureAwait(false);
                    await ReconcileAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Deliberately everything. The next pass re-reads the whole folder from scratch,
                    // so a fault here costs one interval of staleness — where letting it escape costs
                    // every future pass.
                    //
                    // Surviving is only half of it. A pass that fails every time leaves the folder
                    // drifting and looks, from outside, exactly like a pass with nothing to do — so
                    // the survival is recorded rather than only performed.
                    _logger.LogWarning(ex, "A reconciliation pass failed; the loop continues and the next pass retries.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }


    /// <summary>
    /// Defers a scheduled pass while a batch is held.
    ///
    /// <para>
    /// This is the one path that reaches the index without going through the debounce engine, so a
    /// hold cannot reach it the way it reaches everything else. A pass landing inside one reads and
    /// records a file its caller is still part-way through editing — one extra pass over a
    /// half-finished state, which is the whole of what the hold was opened to prevent.
    /// </para>
    ///
    /// <para>
    /// Bounded by construction: a hold stops suppressing once it expires, released or not, so this
    /// cannot wait longer than that. Delaying a safety net by that much costs nothing worth having —
    /// it is here to catch what the watcher missed, not to meet a deadline.
    /// </para>
    /// </summary>
    private async Task WaitForHoldToClearAsync(CancellationToken cancellationToken)
    {
        while (_hold is not null && _hold.IsHoldActive)
        {
            await Task.Delay(HoldPollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private IEnumerable<string> EnumerateIndexableFiles()
    {
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,

            // A reparse point can point anywhere, including above the root or back into this tree.
            // Following one turns a bounded walk into an unbounded one, and can put files outside the
            // watched folder into its index.
            AttributesToSkip = FileAttributes.ReparsePoint,

            // A folder disappearing mid-walk is ordinary here, and it must not end the enumeration
            // for everything after it.
            IgnoreInaccessible = true,
        };

        return Directory
            .EnumerateFiles(_rootPath, "*", options)
            .Where(path => _filter.ShouldReport(path));
    }
}
