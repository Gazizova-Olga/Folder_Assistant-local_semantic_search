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
