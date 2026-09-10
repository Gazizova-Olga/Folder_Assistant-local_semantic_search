using System.IO.Hashing;

namespace FolderAssistant.Indexing.Scanning;

/// <summary>
/// Hashes file contents with xxHash64.
///
/// <para>
/// Deliberately not a cryptographic hash. This answers one question — did these bytes change since
/// the last time we looked — and nothing downstream trusts it as an identity or a signature. The
/// reconciler runs it over every file in the folder on every pass, so the cost is paid at corpus
/// scale for a question a fast non-cryptographic hash answers exactly as well.
/// </para>
///
/// <para>
/// It is a different hash from the one the indexing subsystem uses to address chunk content, and
/// they must not be confused: that one has to be stable across processes and machines because it
/// keys stored rows, while this one only has to be stable within a folder's own history.
/// </para>
/// </summary>
public sealed class XxHash64ContentHasher : IContentHasher
{
    // Large enough that a big file is a handful of reads, small enough not to reach the large object
    // heap (85KB), where per-pass buffers would accumulate in a generation that is not compacted.
    private const int BufferSize = 64 * 1024;

    public async Task<string> HashAsync(string absolutePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        // Share-Read only: a live writer denies this, which is the point. Reading a file mid-write
        // would hash a torn copy and record it as the file's settled content, and the change that
        // finished afterwards would then look like no change at all.
        await using FileStream stream = new(
            absolutePath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = BufferSize,
            });

        XxHash64 hash = new();
        await hash.AppendAsync(stream, cancellationToken).ConfigureAwait(false);

        return Convert.ToHexString(hash.GetCurrentHash()).ToLowerInvariant();
    }

    /// <summary>
    /// The hash of an empty file, which is a legitimate value rather than a missing one. Named so
    /// that "this file is empty" is never mistaken for "this file could not be read".
    /// </summary>
    public static string EmptyContentHash { get; } =
        Convert.ToHexString(XxHash64.Hash([])).ToLowerInvariant();
}
