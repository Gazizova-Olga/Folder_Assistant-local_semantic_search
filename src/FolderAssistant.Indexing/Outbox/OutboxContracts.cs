using FolderAssistant.Indexing.Scanning;

namespace FolderAssistant.Indexing.Outbox;

/// <summary>What a queued delivery asks the embedding side to do.</summary>
public enum DeliveryKind
{
    /// <summary>Embed the file's current content, replacing whatever was embedded for it before.</summary>
    Upsert,

    /// <summary>Remove everything embedded for the file.</summary>
    Delete,
}

/// <summary>One queued delivery, as claimed from the outbox.</summary>
/// <param name="Id">The operation's own id, for recording what became of it.</param>
/// <param name="RelativePath">The file's key.</param>
/// <param name="Kind">What to deliver.</param>
/// <param name="Attempts">How many attempts have already failed.</param>
public sealed record OutboxOp(long Id, string RelativePath, DeliveryKind Kind, int Attempts);

/// <summary>
/// A file as the dispatcher needs it at delivery time: its record, and the content last delivered for it.
/// </summary>
/// <param name="File">The file as the writers last recorded it.</param>
/// <param name="LastSyncedHash">
/// The content hash last delivered and confirmed, or <see langword="null"/> if none has been. Written by
/// the dispatcher alone; the writers never state it.
/// </param>
public sealed record DeliveryRecord(FileRecord File, string? LastSyncedHash);

/// <summary>What the embedding side is told about a delivered file, beside its content.</summary>
/// <param name="AbsolutePath">Where the file is on disk.</param>
/// <param name="Size">Length in bytes as recorded.</param>
/// <param name="CreatedUtc">The file's own creation time as recorded.</param>
/// <param name="Extension">The file's extension, including the dot.</param>
/// <param name="ContentHash">The change-detection hash of the content as recorded.</param>
public sealed record FileMetadata(string AbsolutePath, long Size, DateTime CreatedUtc, string Extension, string ContentHash);

/// <summary>
/// The seam the application implements to turn one file into embedded, searchable content. The library
/// knows nothing about chunking, embedding or vectors; this is the whole of what crosses back.
/// </summary>
public interface IVectorizationService
{
    /// <summary>Embeds the file under <paramref name="docId"/>, replacing what was embedded before.</summary>
    Task UpsertAsync(string docId, Stream content, FileMetadata metadata, CancellationToken cancellationToken);

    /// <summary>Removes everything embedded under <paramref name="docId"/>. Nothing embedded is not an error.</summary>
    Task DeleteAsync(string docId, CancellationToken cancellationToken);
}

/// <summary>
/// The outbox as the dispatcher sees it: a durable log of deliveries, plus the one piece of per-file state
/// the dispatcher owns. Implemented by the application, in the same store as the file records, so that
/// recording a change and queuing its delivery are one write.
/// </summary>
public interface IOutboxStore
{
    /// <summary>
    /// Returns operations left in flight to the queue. A process that stopped mid-delivery never recorded
    /// an outcome, and delivery is at-least-once: the operation is simply tried again.
    /// </summary>
    Task RequeueInFlightAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Claims up to <paramref name="max"/> operations that are due, oldest first, and marks them in flight.
    /// </summary>
    Task<IReadOnlyList<OutboxOp>> ClaimDueAsync(int max, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// The file as currently recorded, or <see langword="null"/> if it is not recorded as present. Read
    /// before an upsert for what to deliver, and before a delete to tell whether the file has come back
    /// since its removal was queued — a removal is deliverable only while the record it came from is gone.
    /// </summary>
    Task<DeliveryRecord?> ReadForDeliveryAsync(string relativePath, CancellationToken cancellationToken);

    /// <summary>
    /// Records that <paramref name="expectedContentHash"/> was delivered — <strong>only if the file's
    /// recorded content is still that hash</strong>. Returns <see langword="false"/>, writing nothing, when
    /// the file has moved on or is gone.
    ///
    /// <para>
    /// Delivery is a round trip, and a file can be rewritten while it happens. Writing the record read before
    /// the delivery back afterwards would restore the old hash over the new one, and the delivery already
    /// queued for the new content would then find it "already delivered" and skip itself — leaving the file
    /// on stale content with nothing left to correct it. Losing the mark is safe: the queued delivery
    /// repeats the work. Losing the edit is not.
    /// </para>
    /// </summary>
    Task<bool> TryMarkSyncedAsync(string relativePath, string expectedContentHash, CancellationToken cancellationToken);

    /// <summary>Retires a delivered operation.</summary>
    Task MarkDoneAsync(long opId, CancellationToken cancellationToken);

    /// <summary>Returns a failed operation to the queue, due again at <paramref name="nextAttemptUtc"/>.</summary>
    Task RescheduleAsync(long opId, int attempts, DateTimeOffset nextAttemptUtc, string error, CancellationToken cancellationToken);

    /// <summary>
    /// Retires an operation that will not be tried again, as <em>failed</em> rather than done, keeping the
    /// error that ended it.
    /// </summary>
    Task MarkAbandonedAsync(long opId, int attempts, string error, CancellationToken cancellationToken);

    /// <summary>
    /// Invited to discard the record of deliveries that succeeded and are older than <paramref name="retention"/>,
    /// returning how many it discarded. Called at the same quiet moment as <see cref="CheckpointAsync"/> and
    /// before it, so the space a prune frees is what the checkpoint then reclaims.
    ///
    /// <para>
    /// <strong>Only operations that succeeded.</strong> A delivery abandoned after its attempt limit is the
    /// record that a file is not in the index, and its one other symptom is a search that quietly does not
    /// find it — so a failed row is kept however old it is, and it is kept whether or not anyone is looking.
    /// The retention window exists because a row that just succeeded is still worth reading while someone is
    /// working out what a burst did.
    /// </para>
    ///
    /// <para>
    /// Advisory, like the checkpoint: a store that keeps nothing to prune does nothing, and one that cannot
    /// prune right now may fail without consequence — an outbox carrying rows it no longer needs costs disk,
    /// never correctness.
    /// </para>
    /// </summary>
    Task<int> PruneDeliveredAsync(TimeSpan retention, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(0);

    /// <summary>
    /// Invited to reclaim what a burst of deliveries left behind — for a database under a write-ahead log,
    /// to fold the log back in. Called only when a drain that delivered work finds the outbox empty, never
    /// per operation and never while idle. Advisory: a store with no such concept does nothing, and one that
    /// cannot take it right now may fail without consequence.
    /// </summary>
    Task CheckpointAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
