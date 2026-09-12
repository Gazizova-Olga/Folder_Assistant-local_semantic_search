using System.Security.Cryptography;

namespace FolderAssistant.Indexing.Scanning;

/// <summary>
/// Hashes a file's bytes with SHA-256.
///
/// <para>
/// Change detection alone would be served by a faster, non-cryptographic hash. This one is chosen for
/// a different reason: the store this library writes through shares its file records with the
/// application's whole-folder pass, which records its own hash of the same file in the same column.
/// Two writers of one record hashing differently would not fail — the first comparison after a
/// whole-folder pass would find every file modified and queue the entire corpus for delivery again,
/// correctly, at full cost, with nothing to say it happened. So the digest is the one the application
/// already computes, over exactly what it computes it over.
/// </para>
///
/// <para>
/// It hashes the bytes, not the decoded text. Decoding drops a byte-order mark and replaces sequences
/// it cannot read, so a hash of the text and a hash of the bytes disagree on precisely the files nobody
/// would think to check.
/// </para>
/// </summary>
public sealed class Sha256ContentHasher : IContentHasher
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

        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
