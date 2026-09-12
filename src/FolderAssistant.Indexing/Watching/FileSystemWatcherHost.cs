using System.Threading.Channels;

namespace FolderAssistant.Indexing.Watching;

/// <summary>
/// Watches a folder and publishes settled changes, one per path per burst.
///
/// <para>
/// Three things it deliberately does not do. It does not report what changed inside a file — the
/// consumer re-diffs by content hash, so a coarse signal is both sufficient and harder to be wrong
/// about. It does not treat a lost event as fatal, because the operating system drops events when
/// its buffer overflows and a periodic reconcile is what heals that. And it does not guarantee a
/// change is real: a touched file with identical content still settles and is reported, and the
/// consumer discovers it has nothing to do.
/// </para>
///
/// <para>
/// Renames are reported as a delete and a create rather than as a move. That is what a rename
/// means to an index keyed on path, and it is also the shape an atomic save takes — so treating
/// the two identically means the save path needs no special case.
/// </para>
///
/// <para>
/// It is also the entry point for a writer inside this process reporting a change it has already
/// made (<see cref="IIndexChangeNotifier"/>). Those reports join the watcher's own events in the
/// same debouncer rather than going around it, so a file written several times in one burst still
/// costs one settled change however the writes were discovered.
/// </para>
/// </summary>
public sealed class FileSystemWatcherHost : IIndexChangeNotifier, IAsyncDisposable
{
    private readonly string _rootPath;
    private readonly TimeSpan _quietWindow;
    private readonly ChangeDebouncer _debouncer;
    private readonly IndexablePathFilter _filter;
    private readonly Channel<ObservedChange> _settled;
    private readonly Lock _gate = new();
    private readonly TimeSpan _maxHoldDuration;

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _stopping;
    private Task? _settleLoop;
    private bool _running;
    private int _holdCount;
    private DateTimeOffset _holdStartedUtc;

    /// <param name="rootPath">The folder to watch, including everything beneath it.</param>
    /// <param name="metadataFolderName">
    /// The index's own folder, which must be excluded or indexing feeds itself.
    /// </param>
    /// <param name="quietWindow">
    /// How long a path must go untouched before its change is published.
    /// </param>
    /// <param name="maxHoldDuration">
    /// How long a hold may suppress publishing before it expires by itself. A backstop against a
    /// caller that never releases, not a schedule: a hold released by its owner never reaches it.
    /// </param>
    public FileSystemWatcherHost(
        string rootPath,
        string metadataFolderName,
        TimeSpan quietWindow,
        TimeSpan? maxHoldDuration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        _rootPath = Path.GetFullPath(rootPath);
        _quietWindow = quietWindow;
        _maxHoldDuration = maxHoldDuration ?? TimeSpan.FromMinutes(2);
        _debouncer = new ChangeDebouncer(quietWindow);
        _filter = new IndexablePathFilter(metadataFolderName);

        // Unbounded, because dropping a settled change is the one failure this stage must not add.
        // The raw event burst is already collapsed by the time anything reaches here, so what
        // accumulates is one item per changed file rather than one per keystroke.
        // One reader, but not one writer: the settle loop publishes on its poll, and the release of
        // the last hold publishes on the releasing caller's thread.
        _settled = Channel.CreateUnbounded<ObservedChange>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    }

    /// <summary>Settled changes, in the order they settled. Completes when the host stops.</summary>
    public ChannelReader<ObservedChange> Changes => _settled.Reader;

    public void Start()
    {
        if (_watcher is not null)
        {
            throw new InvalidOperationException("The watcher host is already running.");
        }

        _stopping = new CancellationTokenSource();

        _watcher = new FileSystemWatcher(_rootPath)
        {
            IncludeSubdirectories = true,

            // Size and last-write cover a save; file name covers create, delete and both halves of
            // a rename. Attribute and security changes are deliberately absent: they alter no
            // content, and reacting to them would re-embed a file because something touched its
            // read-only flag.
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };

        _watcher.Created += (_, e) => Observe(e.FullPath, FileChangeKind.Created);
        _watcher.Changed += (_, e) => Observe(e.FullPath, FileChangeKind.Modified);
        _watcher.Deleted += (_, e) => Observe(e.FullPath, FileChangeKind.Deleted);

        _watcher.Renamed += (_, e) =>
        {
            Observe(e.OldFullPath, FileChangeKind.Deleted);
            Observe(e.FullPath, FileChangeKind.Created);
        };

        // Raised when the OS event buffer overflows and events were lost. There is nothing to
        // recover here and nothing to report about a change whose identity is gone — the periodic
        // reconcile is the mechanism that heals it. Swallowing it must not be silent forever, but
        // it must also not tear down the watcher, which would turn a burst of activity into a
        // permanently blind index.
        _watcher.Error += (_, _) => { };

        _watcher.EnableRaisingEvents = true;

        lock (_gate)
        {
            _running = true;
        }

        _settleLoop = Task.Run(() => RunSettleLoopAsync(_stopping.Token));
    }

    /// <inheritdoc/>
    public ValueTask NotifyChangedAsync(string absolutePath, CancellationToken cancellationToken = default)
        => Report(absolutePath, FileChangeKind.Modified, cancellationToken);

    /// <inheritdoc/>
    public ValueTask NotifyDeletedAsync(string absolutePath, CancellationToken cancellationToken = default)
        => Report(absolutePath, FileChangeKind.Deleted, cancellationToken);

    /// <inheritdoc/>
    public IDisposable BeginBatch()
    {
        lock (_gate)
        {
            // Timed from the first hold, not the latest: if nesting pushed the expiry out, a caller
            // opening one per step would have exactly the unbounded hold the expiry rules out.
            if (_holdCount == 0)
            {
                _holdStartedUtc = DateTimeOffset.UtcNow;
            }

            _holdCount++;
        }

        return new Hold(this);
    }

    private void ReleaseHold()
    {
        bool releasedLast;

        lock (_gate)
        {
            if (_holdCount == 0)
            {
                return;
            }

            _holdCount--;
            releasedLast = _holdCount == 0;
        }

        if (releasedLast)
        {
            // The caller has just said its work is finished, so what it accumulated goes now rather
            // than sitting out the rest of a poll interval that is no longer waiting for anything.
            PublishSettled();
        }
    }

    /// <summary>
    /// Whether a hold is suppressing right now. Expiry is decided here rather than by a timer: an
    /// expired hold simply stops answering yes, which costs no thread and cannot itself be leaked.
    /// The caller must already hold <c>_gate</c>.
    /// </summary>
    private bool IsHeldLocked()
        => _holdCount > 0 && DateTimeOffset.UtcNow - _holdStartedUtc < _maxHoldDuration;

    /// <summary>
    /// A handle whose only job is to be disposed. Disposal is idempotent because a caller disposing
    /// twice would otherwise release a hold belonging to someone else — and let that caller's
    /// half-finished work out.
    /// </summary>
    private sealed class Hold(FileSystemWatcherHost owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            owner.ReleaseHold();
        }
    }

    /// <summary>
    /// Records a reported change exactly as if the operating system had raised it: the same
    /// exclusions, the same quiet window, the same folding. A report therefore coalesces with
    /// itself and with the watcher's own events for that path, instead of costing a pass beside
    /// them.
    /// </summary>
    private ValueTask Report(string absolutePath, FileChangeKind kind, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        cancellationToken.ThrowIfCancellationRequested();

        // Canonicalised, because the debouncer keys on the path string while the watcher reports a
        // full one. Two spellings of one file are two pending entries, which is the merging this
        // exists for failing quietly.
        string fullPath = Path.GetFullPath(absolutePath);

        lock (_gate)
        {
            // Nothing drains the debouncer while the host is stopped, so a report taken here would
            // wait in it indefinitely. Dropping it costs nothing: a change made while nothing was
            // watching is what the reconcile at the next start is for.
            if (!_running)
            {
                return ValueTask.CompletedTask;
            }
        }

        Observe(fullPath, kind);

        return ValueTask.CompletedTask;
    }

    private void Observe(string fullPath, FileChangeKind kind)
    {
        if (!_filter.ShouldReport(fullPath))
        {
            return;
        }

        // Events arrive on the watcher's own threads, and a burst arrives on several at once.
        lock (_gate)
        {
            _debouncer.Observe(fullPath, kind, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Polls for paths that have gone quiet. Polling rather than a per-path timer: a burst touching
    /// a thousand files would otherwise schedule a thousand timers to do one pass's work, and the
    /// poll interval is already bounded by the window it is detecting.
    /// </summary>
    private async Task RunSettleLoopAsync(CancellationToken cancellationToken)
    {
        // A quarter of the window, so a change waits at most a quarter-window past settling, and
        // never below a floor that would spin on a very short window.
        TimeSpan interval = _quietWindow > TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(Math.Max(_quietWindow.TotalMilliseconds / 4, 15))
            : TimeSpan.FromMilliseconds(15);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

                PublishSettled();
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    /// <summary>
    /// Hands every path that has gone quiet to the consumer — unless a hold is open, in which case
    /// they stay in the debouncer, still coalescing, until it is released.
    ///
    /// <para>
    /// A hold covers everything pending, not only what was reported through this type. A caller's
    /// own writes reach the debouncer through the watcher as well, so suppressing one source and
    /// publishing the other would leave the hold suppressing nothing that matters. An editor saving
    /// during a held window waits out the hold, which is a fair price for not indexing the same file
    /// once per step of one edit.
    /// </para>
    /// </summary>
    private void PublishSettled()
    {
        IReadOnlyList<ObservedChange> settled;

        lock (_gate)
        {
            if (IsHeldLocked())
            {
                return;
            }

            settled = _debouncer.DrainSettled(DateTimeOffset.UtcNow);
        }

        foreach (ObservedChange change in settled)
        {
            // Unbounded, so the only refusal is a completed channel — which means disposal, and the
            // drain there has whatever is left.
            _settled.Writer.TryWrite(change);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_watcher is null)
        {
            return;
        }

        // Stop taking new events before draining, so the drain below terminates.
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;

        // Reports are refused from here on. What is already pending is drained below, but
        // accepting a new one during shutdown would record a change nothing is left to publish.
        lock (_gate)
        {
            _running = false;
        }

        if (_stopping is not null)
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
        }

        if (_settleLoop is not null)
        {
            await _settleLoop.ConfigureAwait(false);
            _settleLoop = null;
        }

        // Whatever was still inside its quiet window is published rather than discarded. The edits
        // are real and already made; dropping them would leave the index stale until a reconcile
        // happened to notice, which is a worse answer than reporting them a moment early.
        IReadOnlyList<ObservedChange> remaining;

        lock (_gate)
        {
            remaining = _debouncer.DrainAll();
        }

        foreach (ObservedChange change in remaining)
        {
            _settled.Writer.TryWrite(change);
        }

        _settled.Writer.TryComplete();

        _stopping?.Dispose();
        _stopping = null;
    }
}
