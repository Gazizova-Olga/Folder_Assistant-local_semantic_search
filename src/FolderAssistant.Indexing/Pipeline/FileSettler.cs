namespace FolderAssistant.Indexing.Pipeline;

/// <summary>What a settle probe found.</summary>
internal enum SettleResult
{
    /// <summary>Readable, and unchanged across the probe. Safe to hash.</summary>
    Settled,

    /// <summary>Held by a writer, or still changing. Try again later.</summary>
    Busy,

    /// <summary>Not on disk.</summary>
    Missing,
}

/// <summary>Decides whether a file has finished being written.</summary>
internal interface IFileSettler
{
    Task<SettleResult> SettleAsync(string absolutePath, CancellationToken cancellationToken);
}

/// <summary>
/// Probes whether a file has finished being written before it is hashed.
///
/// <para>
/// A change reaching this stage has already gone quiet for a whole debounce window, but a quiet
/// window measures events, not writers. Two kinds of writer survive it. One holds the file open
/// while it works — a copy, a build step, an editor that keeps its handle — and denies the
/// share-<c>Read</c> open every read here uses. The other shares the file while writing it, so the
/// open succeeds and the content keeps moving underneath. The probe checks for both: an open, and a
/// size and write time that hold still across an interval.
/// </para>
///
/// <para>
/// The hasher's own share-<c>Read</c> open would refuse the first kind as well; checking it here only
/// turns a held file away before the probe spends its interval waiting. The second kind nothing else
/// sees, and hashing it would record a half-written copy as the file's content.
/// </para>
/// </summary>
internal sealed class FileSettler : IFileSettler
{
    private readonly TimeSpan _probeInterval;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;

    public FileSettler(TimeSpan probeInterval)
        : this(probeInterval, Task.Delay)
    {
    }

    /// <param name="wait">
    /// How the probe waits between its two looks. Replaceable so that a change between them can be
    /// staged exactly, rather than raced against a real delay.
    /// </param>
    internal FileSettler(TimeSpan probeInterval, Func<TimeSpan, CancellationToken, Task> wait)
    {
        _probeInterval = probeInterval;
        _wait = wait;
    }

    public async Task<SettleResult> SettleAsync(string absolutePath, CancellationToken cancellationToken)
    {
        (SettleResult result, Snapshot before) = Look(absolutePath);

        if (result != SettleResult.Settled)
        {
            return result;
        }

        await _wait(_probeInterval, cancellationToken).ConfigureAwait(false);

        (result, Snapshot after) = Look(absolutePath);

        if (result != SettleResult.Settled)
        {
            return result;
        }

        return before == after ? SettleResult.Settled : SettleResult.Busy;
    }

    private static (SettleResult Result, Snapshot Snapshot) Look(string absolutePath)
    {
        try
        {
            FileInfo info = new(absolutePath);

            if (!info.Exists)
            {
                return (SettleResult.Missing, default);
            }

            Snapshot snapshot = new(info.Length, info.LastWriteTimeUtc);

            // Opened and closed at once: the question is only whether a writer denies it.
            new FileStream(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read).Dispose();

            return (SettleResult.Settled, snapshot);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return (SettleResult.Missing, default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (SettleResult.Busy, default);
        }
    }

    private readonly record struct Snapshot(long Length, DateTime LastWriteUtc);
}
