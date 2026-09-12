using FolderAssistant.Indexing.Outbox;
using FolderAssistant.Indexing.Pipeline;

namespace FolderAssistant.Indexing;

/// <summary>
/// Everything a <see cref="FolderIndexer"/> needs to know about the folder it indexes and how to
/// pace itself. Data only: the indexer builds its own components from it.
/// </summary>
public sealed record FolderIndexerOptions
{
    /// <summary>The folder to index, including everything beneath it.</summary>
    public required string RootPath { get; init; }

    /// <summary>The index's own folder, by its configured name, which is never indexed.</summary>
    public required string MetadataFolderName { get; init; }

    /// <summary>
    /// The extensions, with their leading dot, of the files this system reads. <see langword="null"/>
    /// indexes every extension. See <see cref="Watching.IndexablePathFilter"/>.
    /// </summary>
    public IReadOnlyCollection<string>? IndexableExtensions { get; init; }

    /// <summary>The largest file that is part of the corpus.</summary>
    public long MaxContentBytes { get; init; } = long.MaxValue;

    /// <summary>How long a path must go untouched before its change is published.</summary>
    public TimeSpan QuietWindow { get; init; } = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// How long a hold may suppress publishing before it expires by itself. <see langword="null"/>
    /// takes the watcher's own default.
    /// </summary>
    public TimeSpan? MaxHoldDuration { get; init; }

    /// <summary>
    /// How often the whole folder is compared against the index, for the changes the watcher never
    /// reported. Zero runs no periodic pass; the pass at start still runs.
    /// </summary>
    public TimeSpan ReconciliationInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How many files a reconciliation pass hashes at once. Zero means the processor count.</summary>
    public int HashingParallelism { get; init; }

    /// <summary>How long the settle probe waits between its two looks at a changed file.</summary>
    public TimeSpan SettleProbeInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long a change that met a busy file waits before it is tried again.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Total tries for one change before it is left to the next reconciliation pass.</summary>
    public int MaxAttempts { get; init; } = ChangePipeline.DefaultMaxAttempts;

    /// <summary>How the outbox is drained: attempts, backoff, parallelism, poll interval.</summary>
    public OutboxDispatcherOptions Dispatcher { get; init; } = new();
}
