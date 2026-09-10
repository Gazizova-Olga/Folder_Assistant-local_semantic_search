namespace FolderAssistant.Indexing.Watching;

/// <summary>
/// Decides whether a path is worth reporting at all.
///
/// <para>
/// It is a type of its own rather than a branch inside the watcher because the watcher's own input
/// arrives from the operating system, which cannot be made to produce a chosen sequence of events
/// on demand. Testing an exclusion through a real <see cref="FileSystemWatcher"/> means creating
/// files and waiting, which asserts the machine's timing as much as the rule. Separated, the rule
/// is a function.
/// </para>
///
/// <para>
/// <strong>Excluding the metadata folder is not an optimisation.</strong> The index lives inside
/// the folder it indexes, so every write the indexer makes lands under the tree it is watching.
/// Unfiltered, indexing a file causes a write, which causes an event, which causes indexing —
/// with no idle state to settle into.
/// </para>
/// </summary>
internal sealed class IndexablePathFilter
{
    /// <summary>
    /// Directories whose contents are build output, version-control internals, or restored
    /// dependencies. Matching the set the application's own scanner skips, so the two walkers
    /// cannot disagree about what the corpus is.
    /// </summary>
    private static readonly string[] IgnoredDirectoryNames = [".git", ".vs", "bin", "obj", "node_modules"];

    /// <summary>
    /// Must stay a <c>char[]</c>. Passing the two separators as loose arguments binds to
    /// <c>Split(char, int)</c> rather than the params overload, because a char converts implicitly to
    /// int — so the alternate separator silently becomes a result count. That exact mis-binding
    /// already shipped once in this repository, in the application's own ignore filter.
    /// </summary>
    private static readonly char[] PathSeparators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private readonly string _metadataFolderName;
    private readonly StringComparison _comparison;

    public IndexablePathFilter(string metadataFolderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataFolderName);

        _metadataFolderName = metadataFolderName;

        // The configured name is compared the way the platform compares paths, for the same reason
        // the debouncer keys on one.
        _comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    }

    public bool ShouldReport(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // The transient file an atomic write leaves beside its target. It exists for milliseconds
        // and holds a half-written copy of a document that is about to be reported in its own
        // right, so indexing it is wasted work at best and a torn document at worst.
        if (path.EndsWith(".tmp", _comparison))
        {
            return false;
        }

        return !path.Split(PathSeparators).Any(IsExcludedSegment);
    }

    private bool IsExcludedSegment(string segment)
        => segment.Equals(_metadataFolderName, _comparison)
            || IgnoredDirectoryNames.Any(ignored => segment.Equals(ignored, _comparison));
}
