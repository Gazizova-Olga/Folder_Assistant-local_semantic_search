namespace FolderAssistant.Indexing.Watching;

/// <summary>
/// Collapses a burst of raw filesystem events into one settled change per path.
///
/// <para>
/// A single save is rarely a single event. Editors write, truncate, rewrite and rename; a build
/// touches hundreds of files in a second. Acting on each event means hashing and embedding a file
/// several times for one edit, and embedding it while it is still being written.
/// </para>
///
/// <para>
/// It holds no timer and reads no clock. The caller supplies "now" on every call, so the settling
/// behaviour can be tested by advancing a variable rather than by sleeping — which is the
/// difference between a test that asserts the rule and one that asserts the machine was fast
/// enough. The timing belongs to whoever owns the loop.
/// </para>
/// </summary>
internal sealed class ChangeDebouncer
{
    private readonly TimeSpan _quietWindow;
    private readonly Dictionary<string, Pending> _pending;

    public ChangeDebouncer(TimeSpan quietWindow)
    {
        if (quietWindow < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(quietWindow), quietWindow, "The quiet window cannot be negative.");
        }

        _quietWindow = quietWindow;

        // Paths compare the way the platform's filesystem does. On Windows a rename that only
        // changes case would otherwise register as two different files, and the pair would settle
        // as a create plus a delete of the same document.
        _pending = new Dictionary<string, Pending>(
            OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether anything is waiting to settle. Lets a caller idle instead of polling.</summary>
    public bool HasPending => _pending.Count > 0;

    /// <summary>
    /// Records one raw event, folding it into whatever is already pending for that path and
    /// restarting that path's quiet window.
    /// </summary>
    public void Observe(string path, FileChangeKind kind, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!_pending.TryGetValue(path, out Pending existing))
        {
            _pending[path] = new Pending(kind, now);
            return;
        }

        FileChangeKind? folded = Fold(existing.Kind, kind);

        if (folded is null)
        {
            // A file created and deleted inside one window never existed as far as anything
            // downstream is concerned. Emitting the pair would cost an index and an un-index of a
            // document that is already gone — which is the common shape of a scratch file, and of
            // the temporary file an atomic save writes beside its target.
            _pending.Remove(path);
            return;
        }

        _pending[path] = new Pending(folded.Value, now);
    }

    /// <summary>
    /// Returns every path that has been quiet for the whole window and forgets them. Paths still
    /// receiving events stay pending.
    /// </summary>
    public IReadOnlyList<ObservedChange> DrainSettled(DateTimeOffset now)
    {
        if (_pending.Count == 0)
        {
            return [];
        }

        ObservedChange[] settled =
        [
            .. _pending
                .Where(entry => now - entry.Value.LastSeen >= _quietWindow)
                .Select(entry => new ObservedChange(entry.Key, entry.Value.Kind)),
        ];

        // Materialised before removing, because the removal invalidates the enumeration it came from.
        foreach (ObservedChange change in settled)
        {
            _pending.Remove(change.Path);
        }

        return settled;
    }

    /// <summary>
    /// Releases everything pending regardless of how recently it was seen, for a shutdown that
    /// should not lose the edits it is holding.
    /// </summary>
    public IReadOnlyList<ObservedChange> DrainAll()
    {
        if (_pending.Count == 0)
        {
            return [];
        }

        ObservedChange[] all = [.. _pending.Select(entry => new ObservedChange(entry.Key, entry.Value.Kind))];
        _pending.Clear();

        return all;
    }

    /// <summary>
    /// Combines what was already pending with what just happened. <see langword="null"/> means the
    /// two annihilate and nothing should be reported.
    ///
    /// <para>
    /// The kind that survives is the one that describes the *net* effect on a consumer holding no
    /// prior state. A file created and then written is still, to that consumer, a new file. A file
    /// deleted and recreated within the window is one it may already know about, so it is reported
    /// as a modification rather than as a create it might refuse as a duplicate.
    /// </para>
    /// </summary>
    private static FileChangeKind? Fold(FileChangeKind pending, FileChangeKind observed)
        => (pending, observed) switch
        {
            (FileChangeKind.Created, FileChangeKind.Deleted) => null,
            (FileChangeKind.Created, _) => FileChangeKind.Created,
            (FileChangeKind.Deleted, FileChangeKind.Created) => FileChangeKind.Modified,
            (FileChangeKind.Deleted, _) => FileChangeKind.Deleted,
            _ => observed,
        };

    private readonly record struct Pending(FileChangeKind Kind, DateTimeOffset LastSeen);
}
