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
/// </summary>
public sealed class FileSystemWatcherHost : IAsyncDisposable
{
    private readonly string _rootPath;
    private readonly TimeSpan _quietWindow;
    private readonly ChangeDebouncer _debouncer;
    private readonly IndexablePathFilter _filter;
    private readonly Channel<ObservedChange> _settled;
    private readonly Lock _gate = new();

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _stopping;
    private Task? _settleLoop;

    /// <param name="rootPath">The folder to watch, including everything beneath it.</param>
    /// <param name="metadataFolderName">
    /// The index's own folder, which must be excluded or indexing feeds itself.
    /// </param>
    /// <param name="quietWindow">
    /// How long a path must go untouched before its change is published.
    /// </param>
    public FileSystemWatcherHost(string rootPath, string metadataFolderName, TimeSpan quietWindow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        _rootPath = Path.GetFullPath(rootPath);
        _quietWindow = quietWindow;
        _debouncer = new ChangeDebouncer(quietWindow);
        _filter = new IndexablePathFilter(metadataFolderName);

        // Unbounded, because dropping a settled change is the one failure this stage must not add.
        // The raw event burst is already collapsed by the time anything reaches here, so what
        // accumulates is one item per changed file rather than one per keystroke.
        _settled = Channel.CreateUnbounded<ObservedChange>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
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

        _settleLoop = Task.Run(() => RunSettleLoopAsync(_stopping.Token));
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

                IReadOnlyList<ObservedChange> settled;

                lock (_gate)
                {
                    settled = _debouncer.DrainSettled(DateTimeOffset.UtcNow);
                }

                foreach (ObservedChange change in settled)
                {
                    await _settled.Writer.WriteAsync(change, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping.
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
