using System.Threading.Channels;

namespace FolderAssistant.Indexing;

/// <summary>Something in the analyzed folder may have changed, and why we think so.</summary>
internal sealed record FolderChangeSignal(String Reason);

/// <summary>
/// Says that the analyzed folder may have changed.
///
/// <para>
/// The signal is deliberately coarse: it names no file, and a consumer cannot learn from it what
/// was edited. That is not an omission. The indexing pass rescans and diffs by content hash on
/// every run regardless, so per-file detail would be gathered, carried, and then ignored — while
/// making the feed responsible for being complete and correct about a set of events that cannot be
/// obtained reliably in the first place.
/// </para>
/// </summary>
internal interface IFileChangeFeed : IDisposable
{
	/// <summary>Begins watching. Signals raised before this are not delivered.</summary>
	void Start();

	IAsyncEnumerable<FolderChangeSignal> ReadAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A change feed over <see cref="FileSystemWatcher"/>.
///
/// <para>
/// The watcher is unreliable in two specific ways, and the coarse signal is what makes both
/// survivable. Its internal buffer overflows under a burst and the events in it are simply lost,
/// so an overflow is reported as "assume everything changed" rather than as an attempt to work out
/// what was missed. And an editor saving a file atomically writes a temporary file and renames it
/// over the original, which arrives as delete-then-create rather than as a change — so a feed that
/// tried to describe the edit would describe the wrong thing, while a feed that only says "look
/// again" is right either way.
/// </para>
/// </summary>
internal sealed class FileSystemWatcherChangeFeed : IFileChangeFeed
{
	private static readonly String[] IgnoredDirectorySegments = [".git", ".vs", "bin", "obj", "node_modules"];

	// Must stay a Char[]. Passing the two separators as loose arguments binds to
	// Split(Char, Int32) rather than the params overload, because Char converts implicitly to
	// Int32 — and the alt separator silently becomes a count.
	private static readonly Char[] PathSeparators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

	// Capacity one, dropping writes when full. A burst of file events collapses into a single
	// pending signal, which is all a consumer that rescans the whole folder can act on. Queueing
	// ten of them would buy ten identical passes.
	private readonly Channel<FolderChangeSignal> _channel =
		Channel.CreateBounded<FolderChangeSignal>(new BoundedChannelOptions(1)
		{
			FullMode = BoundedChannelFullMode.DropWrite,
		});

	private readonly String _rootPath;
	private readonly String _metadataFolderName;
	private readonly TimeSpan _debounce;
	private readonly TimeSpan _reconciliationInterval;

	private FileSystemWatcher? _watcher;
	private Timer? _debounceTimer;
	private Timer? _reconciliationTimer;
	private String _pendingReason = "file change";
	private Boolean _disposed;

	public FileSystemWatcherChangeFeed(
		String rootPath,
		String metadataFolderName,
		TimeSpan debounce,
		TimeSpan reconciliationInterval)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(metadataFolderName);

		this._rootPath = Path.GetFullPath(rootPath);
		this._metadataFolderName = metadataFolderName;
		this._debounce = debounce;
		this._reconciliationInterval = reconciliationInterval;
	}

	public void Start()
	{
		this._debounceTimer = new Timer(this.OnDebounceElapsed, state: null, Timeout.Infinite, Timeout.Infinite);

		// The safety net for everything the watcher never delivers. Because a pass is a full rescan,
		// a periodic tick reconciles whatever drifted without needing to know what was missed.
		if (this._reconciliationInterval > TimeSpan.Zero)
		{
			this._reconciliationTimer = new Timer(
				_ => this.Publish("periodic reconciliation"),
				state: null,
				this._reconciliationInterval,
				this._reconciliationInterval);
		}

		FileSystemWatcher watcher = new(this._rootPath)
		{
			IncludeSubdirectories = true,
			NotifyFilter = NotifyFilters.FileName
				| NotifyFilters.DirectoryName
				| NotifyFilters.LastWrite
				| NotifyFilters.Size,
		};

		watcher.Changed += this.OnFileSystemEvent;
		watcher.Created += this.OnFileSystemEvent;
		watcher.Deleted += this.OnFileSystemEvent;
		watcher.Renamed += this.OnFileSystemEvent;
		watcher.Error += this.OnWatcherError;

		watcher.EnableRaisingEvents = true;
		this._watcher = watcher;
	}

	public IAsyncEnumerable<FolderChangeSignal> ReadAllAsync(CancellationToken cancellationToken)
		=> this._channel.Reader.ReadAllAsync(cancellationToken);

	private void OnFileSystemEvent(Object sender, FileSystemEventArgs e)
	{
		if (this.ShouldIgnore(e.FullPath))
		{
			return;
		}

		this._pendingReason = $"{e.ChangeType}: {Path.GetFileName(e.FullPath)}";

		// Restarts the quiet period on every event, so a run of edits publishes once, after the
		// writing stops, rather than once per keystroke of a file being saved repeatedly.
		this._debounceTimer?.Change(this._debounce, Timeout.InfiniteTimeSpan);
	}

	/// <summary>
	/// The buffer overflowed and events were lost. There is no way to find out which, so the whole
	/// folder is declared suspect — which the consumer handles the same way it handles anything else.
	/// </summary>
	private void OnWatcherError(Object sender, ErrorEventArgs e) => this.Publish("watcher buffer overflow");

	/// <summary>
	/// Keeps the feed from feeding itself. The folder database lives inside the analyzed folder, so
	/// indexing writes files this watcher can see; unfiltered, every pass would trigger the next one
	/// and the folder would index forever.
	/// </summary>
	private Boolean ShouldIgnore(String fullPath)
	{
		String relative = Path.GetRelativePath(this._rootPath, fullPath);
		String[] segments = relative.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);

		foreach (String segment in segments)
		{
			if (String.Equals(segment, this._metadataFolderName, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}

			foreach (String ignored in IgnoredDirectorySegments)
			{
				if (String.Equals(segment, ignored, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
		}

		return false;
	}

	private void OnDebounceElapsed(Object? state) => this.Publish(this._pendingReason);

	private void Publish(String reason) => this._channel.Writer.TryWrite(new FolderChangeSignal(reason));

	public void Dispose()
	{
		if (this._disposed)
		{
			return;
		}

		this._disposed = true;

		if (this._watcher is not null)
		{
			this._watcher.EnableRaisingEvents = false;
			this._watcher.Dispose();
		}

		this._debounceTimer?.Dispose();
		this._reconciliationTimer?.Dispose();
		this._channel.Writer.TryComplete();
	}
}
