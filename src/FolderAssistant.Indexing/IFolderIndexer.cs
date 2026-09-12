using FolderAssistant.Indexing.Watching;

namespace FolderAssistant.Indexing;

/// <summary>
/// The indexing front end as a whole: started once, stopped once, and reachable in between by a
/// writer in this process that wants to report what it changed.
/// </summary>
public interface IFolderIndexer : IIndexChangeNotifier, IAsyncDisposable
{
    /// <summary>Whether the loops are running. False before <see cref="StartAsync"/> and after <see cref="StopAsync"/>.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Compares the folder against the index once, for whatever changed while nothing was watching,
    /// and then keeps the index in step with the folder until stopped.
    /// </summary>
    /// <exception cref="InvalidOperationException">Already running.</exception>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops watching, records every change already observed, and ends the loops. Does nothing when
    /// not running.
    /// </summary>
    /// <param name="cancellationToken">
    /// Bounds the wait for observed changes to be recorded. A caller that has run out of patience
    /// cancels it; what was not recorded by then is the next start's reconciliation to find.
    /// </param>
    Task StopAsync(CancellationToken cancellationToken = default);
}
