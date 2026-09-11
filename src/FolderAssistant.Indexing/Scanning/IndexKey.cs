namespace FolderAssistant.Indexing.Scanning;

/// <summary>
/// The key a file is recorded under: its path relative to the watched root, forward-slashed.
///
/// <para>
/// One rule, because two writers use it — the reconciler's walk and the per-change path. Keyed
/// differently, each would read the other's record as a different file: the file would be added a
/// second time under the second key, and the first record would never be removed, because the
/// writer that could remove it does not recognise it.
/// </para>
/// </summary>
internal static class IndexKey
{
    /// <param name="rootPath">The watched root, already normalised with <see cref="Path.GetFullPath(string)"/>.</param>
    /// <param name="absolutePath">A file beneath it.</param>
    public static string For(string rootPath, string absolutePath)
        => Path.GetRelativePath(rootPath, absolutePath).Replace(Path.DirectorySeparatorChar, '/');
}
