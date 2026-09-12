namespace FolderAssistant.Indexing.Watching;

/// <summary>
/// What happened to a file, as coarsely as the consumer can act on.
/// </summary>
public enum FileChangeKind
{
    /// <summary>The file did not exist and now does.</summary>
    Created,

    /// <summary>The file existed and its contents may differ.</summary>
    Modified,

    /// <summary>The file existed and no longer does.</summary>
    Deleted,
}

/// <summary>
/// One settled change: a path, and what became of it.
///
/// <para>
/// The signal is deliberately coarse. It carries no revision, no byte range and no timestamp,
/// because the consumer re-diffs by content hash anyway — and a signal that promised more would
/// have to be right about it. Being vague is what makes this robust against the two ways
/// filesystem watching is unreliable: events dropped when the OS buffer overflows, and a save
/// implemented as write-temp-then-rename, which is a create and a delete rather than a write.
/// </para>
/// </summary>
public sealed record ObservedChange(string Path, FileChangeKind Kind);

/// <summary>
/// Reports a change made by a writer inside this process, instead of waiting for the operating
/// system to report it back.
///
/// <para>
/// The front end normally <em>discovers</em> changes, which is the only option for an edit made by
/// anything outside this process — an editor, a checkout, a build. It is the wrong one for an edit
/// this process makes itself: that writer knows the path the moment its handle closes, so the file
/// is already settled and the discovery round-trip buys nothing but delay.
/// </para>
///
/// <para>
/// A report is fed into the same settling path the watcher's own events go through, never past it.
/// That routing is the requirement rather than an implementation detail. Several writes to one file
/// in quick succession must cost one index pass, exactly as they do for an editor saving
/// repeatedly; and a report must merge with the watcher's own events for that file, which are
/// raised as well, rather than racing them into a second, duplicate pass. A path that bypassed
/// settling would make reporting a change <em>worse</em> than being rediscovered.
/// </para>
///
/// <para>
/// Reporting is advisory. It changes when a change is indexed, never whether: the watcher and the
/// periodic reconcile converge on the same state regardless, so a report that is dropped — because
/// nothing is running to receive it, or because the path is one that is never reported — costs
/// latency and nothing else. A caller must not fail its own operation over one.
/// </para>
/// </summary>
public interface IIndexChangeNotifier
{
    /// <summary>
    /// Reports that <paramref name="absolutePath"/> was written in place.
    /// </summary>
    ValueTask NotifyChangedAsync(string absolutePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports that <paramref name="absolutePath"/> was deleted.
    /// </summary>
    ValueTask NotifyDeletedAsync(string absolutePath, CancellationToken cancellationToken = default);
}
