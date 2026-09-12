namespace FolderAssistant.Indexing.Watching;

/// <summary>
/// Decides whether a path is worth reporting at all, and whether a file on disk is worth indexing.
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
///
/// <para>
/// <strong>One instance serves every walker.</strong> The watcher, the reconciler and the per-change
/// path each decide what is in the corpus, and if they decided differently a file could be recorded
/// by one and refused by the next — which looks exactly like a file nobody ever saved.
/// </para>
/// </summary>
public sealed class IndexablePathFilter
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
    private readonly HashSet<string>? _indexableExtensions;
    private readonly long _maxContentBytes;
    private readonly StringComparison _comparison;

    /// <param name="metadataFolderName">The index's own folder, by its configured name.</param>
    /// <param name="indexableExtensions">
    /// The extensions, with their leading dot, of the files this system reads. <see langword="null"/>
    /// reports every extension. Given a list, a file outside it is never reported and never indexed
    /// — the front end would otherwise record and deliver every binary in the folder to a consumer
    /// that refuses each one, which is churn on every pass rather than a fault.
    /// </param>
    /// <param name="maxContentBytes">
    /// The largest file worth indexing. A larger one is not part of the corpus: it is neither
    /// recorded nor delivered, and if the index already holds it, it is removed. Size is a property
    /// of a file on disk rather than of a path, so it is checked by <see cref="ShouldIndex"/> and
    /// not by <see cref="ShouldReport"/>.
    /// </param>
    public IndexablePathFilter(
        string metadataFolderName,
        IEnumerable<string>? indexableExtensions = null,
        long maxContentBytes = long.MaxValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataFolderName);
        ArgumentOutOfRangeException.ThrowIfNegative(maxContentBytes);

        _metadataFolderName = metadataFolderName;
        _maxContentBytes = maxContentBytes;

        // Extensions compare case-insensitively on every platform, matching the application's own
        // allow-list; only folder names follow the platform's rule.
        _indexableExtensions = indexableExtensions is null
            ? null
            : new HashSet<string>(indexableExtensions, StringComparer.OrdinalIgnoreCase);

        // The configured name is compared the way the platform compares paths, for the same reason
        // the debouncer keys on one.
        _comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    }

    /// <summary>
    /// Whether a path is one this system could ever index: outside the excluded folders, not a
    /// transient write, and of an extension it reads. Decided from the path alone, so it can be
    /// asked of a file that no longer exists — a delete is reported by path, and it has to be
    /// reported for exactly the files that were ever recorded.
    /// </summary>
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

        if (_indexableExtensions is not null && !_indexableExtensions.Contains(Path.GetExtension(path)))
        {
            return false;
        }

        return !path.Split(PathSeparators).Any(IsExcludedSegment);
    }

    /// <summary>
    /// Whether a file on disk belongs in the index: everything <see cref="ShouldReport"/> requires,
    /// and a size within the bound.
    /// </summary>
    public bool ShouldIndex(string path, long sizeBytes)
        => sizeBytes <= _maxContentBytes && ShouldReport(path);

    private bool IsExcludedSegment(string segment)
        => segment.Equals(_metadataFolderName, _comparison)
            || IgnoredDirectoryNames.Any(ignored => segment.Equals(ignored, _comparison));
}
