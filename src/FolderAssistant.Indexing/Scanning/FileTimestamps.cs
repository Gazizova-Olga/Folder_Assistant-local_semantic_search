namespace FolderAssistant.Indexing.Scanning;

/// <summary>
/// How a file's creation time is recorded.
///
/// <para>
/// One rule, because two writers record it — the reconciler's walk and the per-change path. If they
/// derived it differently, a file's recorded age would depend on which of them happened to discover
/// it.
/// </para>
///
/// <para>
/// The value is the file's own, read from the filesystem — never the moment a writer ran. A creation
/// date that is really "when this folder was first indexed" is not metadata about the file, and every
/// file in a folder indexed at once would carry the same one.
/// </para>
/// </summary>
internal static class FileTimestamps
{
    /// <summary>What the file API reports when it has no creation time to give.</summary>
    private static readonly DateTime NoCreationTime = DateTime.FromFileTimeUtc(0);

    /// <summary>
    /// The creation time to record for a file seen for the first time. Where none is reported, the
    /// write time stands in: it is the closest true statement available about the file's age, where the
    /// 1601 epoch would date every such file to the same impossible moment.
    /// </summary>
    public static DateTime ResolveCreatedUtc(DateTime creationTimeUtc, DateTime lastWriteTimeUtc)
        => creationTimeUtc <= NoCreationTime ? lastWriteTimeUtc : creationTimeUtc;

    /// <summary>
    /// Reads the creation time to record for the file behind <paramref name="info"/>.
    /// </summary>
    public static DateTime ReadCreatedUtc(FileInfo info)
        => ResolveCreatedUtc(info.CreationTimeUtc, info.LastWriteTimeUtc);

    /// <summary>
    /// A modified file keeps the creation time already recorded for it: editing a file does not create
    /// it.
    /// </summary>
    public static FileRecord KeepRecordedCreation(FileRecord current, FileRecord recorded)
        => current with { CreatedUtc = recorded.CreatedUtc };
}
