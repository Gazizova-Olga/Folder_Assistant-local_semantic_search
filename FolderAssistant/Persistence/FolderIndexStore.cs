using System.Globalization;
using FolderAssistant.Indexing.Outbox;
using FolderAssistant.Indexing.Scanning;
using Microsoft.Data.Sqlite;

namespace FolderAssistant.Persistence;

/// <summary>
/// One file the whole-folder pass embedded, as the pass read it: what it asks the store to record as
/// delivered, under the same condition a delivery is recorded under.
/// </summary>
internal sealed record DeliveredContent(String RelativePath, String ContentHash);

/// <summary>
/// The database behind the indexing library: where file records live, and where deliveries queue.
///
/// <para>
/// The library owns no database on purpose, so that the index and the rest of this application's
/// persistence stay in one file under one set of rules. This is the application's side of that
/// bargain — both of the library's store seams over the same folder database, which is what makes
/// recording a change and queuing its delivery a single write. Split across two stores they could
/// not be, and a change recorded but never queued is a file believed indexed that never is: the
/// next comparison finds the record already matching the disk and concludes there is nothing to do.
/// </para>
///
/// <para>
/// Every write here states the columns it owns and no others (<c>SPEC-121</c>). Nothing reads a
/// record and writes it back whole: a classification writes what the scan observed, a delivery
/// writes the mark, and neither can revert the other by holding a stale copy of it.
/// </para>
///
/// <para>
/// The calls are asynchronous because the seams are, not because the work is — this provider's
/// async methods are its synchronous ones behind a state machine. Everything here already runs on a
/// background thread: a reconciliation pass, a settled change, a delivery. The blocking call is the
/// honest shape, and a completed task is not a claim about it.
/// </para>
/// </summary>
internal sealed class FolderIndexStore : IIndexStore, IOutboxStore
{
	/// <summary>
	/// What a queued delivery's <c>status</c> column holds. The two terminal values are kept apart
	/// deliberately: a queue full of work given up on must not read like one where everything
	/// succeeded.
	/// </summary>
	private const Int32 Pending = 0;
	private const Int32 InFlight = 1;
	private const Int32 Done = 2;
	private const Int32 Failed = 3;

	/// <summary>
	/// A row whose file is gone keeps its place until its removal has been delivered. The active value
	/// is visible to the manifest reader, which must ask the same question of the same column.
	/// </summary>
	internal const String Active = "active";
	private const String Deleted = "deleted";

	/// <summary>
	/// The one statement that writes the delivery mark, conditional on the content it describes. Shared
	/// by the per-delivery path and the whole-folder pass, so the two cannot hold different conditions.
	/// </summary>
	private const String MarkSyncedSql = """
		UPDATE file_manifest
		SET last_synced_hash = $hash, updated_utc = $now
		WHERE file_path = $path AND file_hash = $hash;
		""";

	private readonly String _databasePath;

	public FolderIndexStore(String databasePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

		this._databasePath = databasePath;
	}

	// ── IIndexStore ────────────────────────────────────────────────────────────

	public Task<IReadOnlyDictionary<String, FileRecord>> ReadAllAsync(CancellationToken cancellationToken = default)
	{
		Dictionary<String, FileRecord> records = new(StringComparer.Ordinal);

		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(this._databasePath);
		using SqliteCommand command = connection.CreateCommand();

		command.CommandText = $"""
			SELECT file_path, file_hash, size_bytes, created_utc
			FROM file_manifest
			WHERE status = '{Active}';
			""";

		using SqliteDataReader reader = command.ExecuteReader();

		while (reader.Read())
		{
			FileRecord record = ReadRecord(reader);
			records[record.RelativePath] = record;
		}

		return Task.FromResult<IReadOnlyDictionary<String, FileRecord>>(records);
	}

	public Task<FileRecord?> ReadAsync(String relativePath, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(this._databasePath);
		using SqliteCommand command = connection.CreateCommand();

		command.CommandText = $"""
			SELECT file_path, file_hash, size_bytes, created_utc
			FROM file_manifest
			WHERE file_path = $path AND status = '{Active}';
			""";
		command.Parameters.AddWithValue("$path", relativePath);

		using SqliteDataReader reader = command.ExecuteReader();

		return Task.FromResult(reader.Read() ? ReadRecord(reader) : null);
	}

	/// <summary>
	/// Records what a pass concluded and queues the deliveries it implies, in one transaction.
	/// </summary>
	public Task ApplyAsync(IReadOnlyList<ReconciledChange> changes, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(changes);

		if (changes.Count == 0)
		{
			return Task.CompletedTask;
		}

		using SqliteConnection connection = FolderDatabaseConnection.OpenWrite(this._databasePath);
		using SqliteTransaction transaction = connection.BeginTransaction();

		String now = UtcNow();

		foreach (ReconciledChange change in changes)
		{
			if (change.Delta == FileDelta.Removed)
			{
				MarkRemoved(connection, transaction, change.RelativePath, now);
				Queue(connection, transaction, change.RelativePath, DeliveryKind.Delete, now);

				continue;
			}

			FileRecord record = change.Current
				?? throw new ArgumentException(
					$"A {change.Delta} change carries no record for '{change.RelativePath}'.", nameof(changes));

			RecordFile(connection, transaction, record, now);
			Queue(connection, transaction, change.RelativePath, DeliveryKind.Upsert, now);
		}

		transaction.Commit();

		return Task.CompletedTask;
	}

	// ── IOutboxStore ───────────────────────────────────────────────────────────

	/// <summary>
	/// Returns claimed work to the queue. A process that stopped mid-delivery recorded no outcome for
	/// what it held, and an operation nobody is delivering is one nobody will ever finish.
	/// </summary>
	public Task RequeueInFlightAsync(CancellationToken cancellationToken)
	{
		using SqliteConnection connection = FolderDatabaseConnection.OpenWrite(this._databasePath);
		using SqliteCommand command = connection.CreateCommand();

		command.CommandText = $"UPDATE outbox SET status = {Pending} WHERE status = {InFlight};";
		command.ExecuteNonQuery();

		return Task.CompletedTask;
	}

	/// <summary>
	/// Claims up to <paramref name="max"/> due operations, oldest first, marking them in flight in the
	/// same transaction that read them. Selecting and claiming separately would let two drains take
	/// the same operation and deliver one file twice.
	/// </summary>
	public Task<IReadOnlyList<OutboxOp>> ClaimDueAsync(
		Int32 max,
		DateTimeOffset now,
		CancellationToken cancellationToken)
	{
		if (max <= 0)
		{
			return Task.FromResult<IReadOnlyList<OutboxOp>>([]);
		}

		using SqliteConnection connection = FolderDatabaseConnection.OpenWrite(this._databasePath);
		using SqliteTransaction transaction = connection.BeginTransaction();

		List<OutboxOp> claimed = [];

		using (SqliteCommand due = connection.CreateCommand())
		{
			due.Transaction = transaction;
			due.CommandText = $"""
				SELECT id, file_path, op_type, attempts
				FROM outbox
				WHERE status = {Pending} AND next_attempt_utc <= $now
				ORDER BY id
				LIMIT $max;
				""";
			due.Parameters.AddWithValue("$now", Timestamp(now));
			due.Parameters.AddWithValue("$max", max);

			using SqliteDataReader reader = due.ExecuteReader();

			while (reader.Read())
			{
				claimed.Add(new OutboxOp(
					reader.GetInt64(0),
					reader.GetString(1),
					(DeliveryKind)reader.GetInt32(2),
					reader.GetInt32(3)));
			}
		}

		foreach (OutboxOp op in claimed)
		{
			using SqliteCommand claim = connection.CreateCommand();
			claim.Transaction = transaction;
			claim.CommandText = $"UPDATE outbox SET status = {InFlight} WHERE id = $id;";
			claim.Parameters.AddWithValue("$id", op.Id);
			claim.ExecuteNonQuery();
		}

		transaction.Commit();

		return Task.FromResult<IReadOnlyList<OutboxOp>>(claimed);
	}

	public Task<DeliveryRecord?> ReadForDeliveryAsync(
		String relativePath,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(this._databasePath);
		using SqliteCommand command = connection.CreateCommand();

		command.CommandText = $"""
			SELECT file_path, file_hash, size_bytes, created_utc, last_synced_hash
			FROM file_manifest
			WHERE file_path = $path AND status = '{Active}';
			""";
		command.Parameters.AddWithValue("$path", relativePath);

		using SqliteDataReader reader = command.ExecuteReader();

		if (!reader.Read())
		{
			return Task.FromResult<DeliveryRecord?>(null);
		}

		String? lastSynced = reader.IsDBNull(4) ? null : reader.GetString(4);

		return Task.FromResult<DeliveryRecord?>(new DeliveryRecord(ReadRecord(reader), lastSynced));
	}

	/// <summary>
	/// Records what was delivered, and only while the file still holds the content that was.
	///
	/// <para>
	/// The condition is the whole method. A delivery is a round trip, the file can be rewritten
	/// during it, and writing the mark unconditionally would claim the newer content had been
	/// embedded when the older one was — after which the delivery queued for the newer content sees
	/// its work already done and skips, leaving the file on stale vectors with nothing left to
	/// correct it. Losing a mark is safe by comparison: the queued delivery re-embeds.
	/// </para>
	/// </summary>
	public Task<Boolean> TryMarkSyncedAsync(
		String relativePath,
		String expectedContentHash,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedContentHash);

		using SqliteConnection connection = FolderDatabaseConnection.OpenWrite(this._databasePath);
		using SqliteCommand command = connection.CreateCommand();

		command.CommandText = MarkSyncedSql;
		command.Parameters.AddWithValue("$path", relativePath);
		command.Parameters.AddWithValue("$hash", expectedContentHash);
		command.Parameters.AddWithValue("$now", UtcNow());

		return Task.FromResult(command.ExecuteNonQuery() > 0);
	}

	/// <summary>
	/// Records many deliveries in one transaction, each under exactly the condition
	/// <see cref="TryMarkSyncedAsync"/> holds one to: a file takes the mark only while its recorded
	/// content is still what was delivered. Returns how many did.
	///
	/// <para>
	/// The whole-folder pass embeds a corpus in one write and then says so here. One transaction
	/// rather than one per file because each commit under a write-ahead log is a sync to disk, and
	/// twelve thousand of them would cost a cold start more than the embedding did. The pass calls
	/// this after its vectors have committed and never before: a mark without vectors is a file
	/// believed indexed that is not, where vectors without a mark cost one duplicate delivery.
	/// </para>
	/// </summary>
	public Int32 MarkSynced(IReadOnlyList<DeliveredContent> deliveries)
	{
		ArgumentNullException.ThrowIfNull(deliveries);

		if (deliveries.Count == 0)
		{
			return 0;
		}

		using SqliteConnection connection = FolderDatabaseConnection.OpenWrite(this._databasePath);
		using SqliteTransaction transaction = connection.BeginTransaction();
		using SqliteCommand command = connection.CreateCommand();

		command.Transaction = transaction;
		command.CommandText = MarkSyncedSql;

		SqliteParameter path = command.Parameters.Add("$path", SqliteType.Text);
		SqliteParameter hash = command.Parameters.Add("$hash", SqliteType.Text);
		command.Parameters.AddWithValue("$now", UtcNow());

		Int32 marked = 0;

		foreach (DeliveredContent delivery in deliveries)
		{
			path.Value = delivery.RelativePath;
			hash.Value = delivery.ContentHash;
			marked += command.ExecuteNonQuery();
		}

		transaction.Commit();

		return marked;
	}

	public Task MarkDoneAsync(Int64 opId, CancellationToken cancellationToken)
		=> this.SetOpStatus(opId, Done, attempts: null, error: null);

	public Task RescheduleAsync(
		Int64 opId,
		Int32 attempts,
		DateTimeOffset nextAttemptUtc,
		String error,
		CancellationToken cancellationToken)
	{
		using SqliteConnection connection = FolderDatabaseConnection.OpenWrite(this._databasePath);
		using SqliteCommand command = connection.CreateCommand();

		command.CommandText = $"""
			UPDATE outbox
			SET status = {Pending}, attempts = $attempts, next_attempt_utc = $next, error = $error
			WHERE id = $id;
			""";
		command.Parameters.AddWithValue("$id", opId);
		command.Parameters.AddWithValue("$attempts", attempts);
		command.Parameters.AddWithValue("$next", Timestamp(nextAttemptUtc));
		command.Parameters.AddWithValue("$error", error ?? String.Empty);
		command.ExecuteNonQuery();

		return Task.CompletedTask;
	}

	/// <summary>
	/// Retires an operation nothing will try again as <em>failed</em>, keeping the error that ended
	/// it. Retiring it as done would leave a queue of work given up on indistinguishable from one
	/// where everything succeeded — and an abandoned delivery's only other symptom is a search that
	/// quietly does not find a file that is there.
	/// </summary>
	public Task MarkAbandonedAsync(
		Int64 opId,
		Int32 attempts,
		String error,
		CancellationToken cancellationToken)
		=> this.SetOpStatus(opId, Failed, attempts, error);

	/// <summary>
	/// Discards the rows of deliveries that succeeded and were queued longer ago than
	/// <paramref name="retention"/>. Nothing else: a <see cref="Failed"/> row is the record that a file
	/// is not in the index and is kept however old it is, and a <see cref="Pending"/> or
	/// <see cref="InFlight"/> row is work.
	///
	/// <para>
	/// The window is measured from <c>created_utc</c> — when the operation was <em>queued</em> — because
	/// that is the only time the table records; there is no completion column. The consequence is worth
	/// stating rather than hiding: an operation that spent a day being retried and then succeeded is
	/// eligible immediately, which is the row someone would most want to read. Making the window mean
	/// "since delivered" costs a column and a migration, and is worth it only if anyone is reading these
	/// rows for diagnosis in practice.
	/// </para>
	/// </summary>
	public Task<Int32> PruneDeliveredAsync(TimeSpan retention, DateTimeOffset now, CancellationToken cancellationToken)
	{
		using SqliteConnection connection = FolderDatabaseConnection.OpenWrite(this._databasePath);
		using SqliteCommand command = connection.CreateCommand();

		command.CommandText = $"DELETE FROM outbox WHERE status = {Done} AND created_utc < $before;";
		command.Parameters.AddWithValue("$before", Timestamp(now - retention));

		return Task.FromResult(command.ExecuteNonQuery());
	}

	public Task CheckpointAsync(CancellationToken cancellationToken)
	{
		FolderDatabaseMaintenance.Checkpoint(this._databasePath);

		return Task.CompletedTask;
	}

	// ── writing ────────────────────────────────────────────────────────────────

	/// <summary>
	/// Writes what a scan observed about a file, and nothing else.
	///
	/// <para>
	/// The delivery mark is absent from every branch, which is what keeps a classification from
	/// reverting a delivery recorded while it ran. It is cleared in exactly one case: a row that was
	/// not active. What the mark referred to went with the file's chunks when the removal was
	/// delivered, so a file restored byte-for-byte would otherwise look already delivered and would
	/// never be embedded again — silently, and for as long as it existed.
	/// </para>
	///
	/// <para>
	/// A creation time is recorded once and kept, because editing a file does not create it. Where
	/// none is known the column stays null rather than taking the moment the pass ran, which would
	/// record the crawl as the file's age.
	/// </para>
	/// </summary>
	private static void RecordFile(
		SqliteConnection connection,
		SqliteTransaction transaction,
		FileRecord record,
		String now)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;

		command.CommandText = $"""
			INSERT INTO file_manifest (
				file_id, file_path, file_hash, size_bytes, created_utc, modified_utc, status, updated_utc)
			VALUES ($id, $path, $hash, $size, $created, $now, '{Active}', $now)
			ON CONFLICT(file_path) DO UPDATE SET
				file_hash = excluded.file_hash,
				size_bytes = excluded.size_bytes,
				created_utc = COALESCE(file_manifest.created_utc, excluded.created_utc),
				modified_utc = excluded.modified_utc,
				updated_utc = excluded.updated_utc,
				status = '{Active}',
				last_synced_hash = CASE
					WHEN file_manifest.status = '{Active}' THEN file_manifest.last_synced_hash
					ELSE NULL
				END;
			""";
		command.Parameters.AddWithValue("$id", FileIdentity.For(record.RelativePath));
		command.Parameters.AddWithValue("$path", record.RelativePath);
		command.Parameters.AddWithValue("$hash", record.ContentHash);
		command.Parameters.AddWithValue("$size", record.Size);
		command.Parameters.AddWithValue("$created", CreatedUtcOrNull(record.CreatedUtc));
		command.Parameters.AddWithValue("$now", now);
		command.ExecuteNonQuery();
	}

	/// <summary>
	/// Marks a file gone without removing its row. The row is what the file's chunks hang from, and
	/// clearing those means clearing vectors first — which belongs to the embedding side and arrives
	/// with the delivery this change queues. Reads exclude the row from this moment, so a later pass
	/// does not rediscover the same deletion and queue it again.
	/// </summary>
	private static void MarkRemoved(
		SqliteConnection connection,
		SqliteTransaction transaction,
		String relativePath,
		String now)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;

		command.CommandText = $"""
			UPDATE file_manifest
			SET status = '{Deleted}', updated_utc = $now
			WHERE file_path = $path;
			""";
		command.Parameters.AddWithValue("$path", relativePath);
		command.Parameters.AddWithValue("$now", now);
		command.ExecuteNonQuery();
	}

	private static void Queue(
		SqliteConnection connection,
		SqliteTransaction transaction,
		String relativePath,
		DeliveryKind kind,
		String now)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;

		command.CommandText = $"""
			INSERT INTO outbox (file_path, op_type, status, attempts, created_utc, next_attempt_utc)
			VALUES ($path, $type, {Pending}, 0, $now, $now);
			""";
		command.Parameters.AddWithValue("$path", relativePath);
		command.Parameters.AddWithValue("$type", (Int32)kind);
		command.Parameters.AddWithValue("$now", now);
		command.ExecuteNonQuery();
	}

	private Task SetOpStatus(Int64 opId, Int32 status, Int32? attempts, String? error)
	{
		using SqliteConnection connection = FolderDatabaseConnection.OpenWrite(this._databasePath);
		using SqliteCommand command = connection.CreateCommand();

		command.CommandText = attempts is null
			? $"UPDATE outbox SET status = {status} WHERE id = $id;"
			: $"UPDATE outbox SET status = {status}, attempts = $attempts, error = $error WHERE id = $id;";

		command.Parameters.AddWithValue("$id", opId);

		if (attempts is not null)
		{
			command.Parameters.AddWithValue("$attempts", attempts.Value);
			command.Parameters.AddWithValue("$error", error ?? String.Empty);
		}

		command.ExecuteNonQuery();

		return Task.CompletedTask;
	}

	private static FileRecord ReadRecord(SqliteDataReader reader)
		=> new(
			reader.GetString(0),
			reader.GetString(1),
			reader.GetInt64(2),
			reader.IsDBNull(3)
				? default
				: DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

	/// <summary>
	/// A creation time nobody knew is stored as nothing. <see cref="DateTime"/>'s default is what a
	/// record carries when no filesystem reported one, and writing it as a date would put the year 1
	/// on a file rather than admit the value is missing.
	/// </summary>
	private static Object CreatedUtcOrNull(DateTime createdUtc)
		=> createdUtc == default ? DBNull.Value : createdUtc.ToString("O", CultureInfo.InvariantCulture);

	private static String Timestamp(DateTimeOffset value)
		=> value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

	private static String UtcNow() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
}
