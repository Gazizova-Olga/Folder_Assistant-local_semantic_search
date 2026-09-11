namespace FolderAssistant.Indexing.Scanning;

/// <summary>
/// What the index has recorded about one file.
///
/// <para>
/// Keyed on the path relative to the watched root, forward-slashed, so the record survives the
/// folder being moved and does not depend on which separator the platform reports.
/// </para>
/// </summary>
/// <param name="RelativePath">The file's key, relative to the watched root.</param>
/// <param name="ContentHash">
/// A hash of the file's bytes, for deciding whether it changed. Never empty: a file that could not
/// be hashed is left out of the index's picture entirely rather than recorded with a blank, because
/// a blank becomes the file's stored identity and every other unhashable file then looks like a
/// copy of it.
/// </param>
/// <param name="Size">Length in bytes at the time it was recorded.</param>
/// <param name="CreatedUtc">
/// The file's own creation time as the filesystem reports it, recorded when the file is first seen and
/// kept across edits. Never the time a writer ran.
/// </param>
public sealed record FileRecord(string RelativePath, string ContentHash, long Size, DateTime CreatedUtc);

/// <summary>What comparing one file on disk against the index concluded.</summary>
public enum FileDelta
{
    /// <summary>Present on disk, absent from the index.</summary>
    Added,

    /// <summary>Present in both, with different content.</summary>
    Modified,

    /// <summary>Absent from disk, present in the index.</summary>
    Removed,
}

/// <summary>One file's classification, and its current record where it has one.</summary>
public sealed record ReconciledChange(string RelativePath, FileDelta Delta, FileRecord? Current);

/// <summary>
/// What a reconciliation pass did.
///
/// <para>
/// <paramref name="Skipped"/> is reported rather than folded into the totals, because a pass that
/// examined nine hundred of a thousand files is a different event from one that examined all of
/// them, and the difference is invisible if only the changes are counted.
/// </para>
/// </summary>
public sealed record ReconcileResult(int Examined, int Skipped, IReadOnlyList<ReconciledChange> Changes);

/// <summary>
/// Where per-file state is kept. The library does not own a database — the application supplies
/// one, so that the index and the rest of the application's persistence stay in one place with one
/// writer.
/// </summary>
public interface IIndexStore
{
    /// <summary>Every file currently recorded, keyed by relative path.</summary>
    Task<IReadOnlyDictionary<string, FileRecord>> ReadAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// One file's record, or <see langword="null"/> when the index has none. The per-change path
    /// reads this rather than <see cref="ReadAllAsync"/>: a single event concerns a single file, and
    /// reading every record to answer it is a cost that grows with the corpus on every save.
    /// </summary>
    Task<FileRecord?> ReadAsync(string relativePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a set of conclusions. Applied as one unit: a partially applied pass would leave the
    /// index describing a folder that never existed at any moment.
    ///
    /// <para>
    /// Applying a change also queues its delivery — an upsert for an added or modified file, a delete for a
    /// removed one — <strong>in the same unit</strong>. Recorded without being queued, a change would be
    /// believed and never delivered, and nothing would come back for it: the next comparison finds the
    /// record already matching the disk.
    /// </para>
    /// </summary>
    Task ApplyAsync(IReadOnlyList<ReconciledChange> changes, CancellationToken cancellationToken = default);
}

/// <summary>Hashes a file's contents for change detection.</summary>
public interface IContentHasher
{
    /// <summary>
    /// Hashes the file at <paramref name="absolutePath"/>. Throws <see cref="IOException"/> if the
    /// file cannot be read, which callers are expected to treat as ordinary.
    /// </summary>
    Task<string> HashAsync(string absolutePath, CancellationToken cancellationToken = default);
}
