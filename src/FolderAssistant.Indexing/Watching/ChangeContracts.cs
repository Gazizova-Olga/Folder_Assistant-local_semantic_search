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
    /// Reports that <paramref name="absolutePath"/> is a file that did not exist before.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="NotifyChangedAsync"/> because coalescing treats the two differently: a
    /// create and a delete inside one window annihilate, so a file written and cleaned up inside one
    /// costs nothing at all. Reported as a modification instead, that same pair folds to a deletion —
    /// of a path that was never indexed, which ends in the same place only because the consumer looks
    /// for a record to remove and finds none. That is luck, and it runs out the moment either rule
    /// changes.
    /// </remarks>
    ValueTask NotifyCreatedAsync(string absolutePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports that <paramref name="absolutePath"/> was written in place.
    /// </summary>
    ValueTask NotifyChangedAsync(string absolutePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports that <paramref name="absolutePath"/> was deleted.
    /// </summary>
    ValueTask NotifyDeletedAsync(string absolutePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Holds everything back until the returned handle is disposed, and then lets it go as one
    /// batch.
    ///
    /// <para>
    /// The quiet window merges writes that are close together in time. It cannot merge writes
    /// separated by the caller stopping to think between them, which is the ordinary shape of a
    /// multi-step edit: each write then lands in a window of its own and costs its own pass. Only
    /// the caller knows where its work actually ends, so a hold is how it says so.
    /// </para>
    ///
    /// <para>
    /// Holds nest and are counted; the last one released is what lets the batch go. A hold that is
    /// never released <em>expires</em> rather than holding forever — a leaked handle has to cost a
    /// bounded delay, not an index that stops converging for the life of the process. Disposal is
    /// idempotent, and disposing one that has already expired does nothing.
    /// </para>
    /// </summary>
    IDisposable BeginBatch();
}

/// <summary>
/// Whether a batch is being held back right now.
///
/// <para>
/// Anything that reaches the index without passing through the debouncer has to ask. A hold is
/// enforced where changes are published, which covers everything that goes through there and
/// nothing else — so a pass that reads the folder and writes the index directly would run straight
/// through a hold and record the half-finished state the hold exists to keep out.
/// </para>
/// </summary>
public interface IBatchHoldState
{
    /// <summary>
    /// True while a hold is suppressing. False once the last one is released, and false once a hold
    /// nobody released has expired.
    /// </summary>
    bool IsHoldActive { get; }
}
