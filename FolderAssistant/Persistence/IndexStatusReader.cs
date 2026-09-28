using Microsoft.Data.Sqlite;

namespace FolderAssistant.Persistence;

/// <summary>One file the index gave up on, and why.</summary>
internal sealed record FailedFile(String RelativePath, String? Error, Int32 Attempts);

/// <summary>
/// What the index holds, counted: how many files are delivered, how many are still owed a delivery, and
/// how many were given up on.
///
/// <para>
/// <paramref name="FailedFiles"/> is a sample bounded by what the caller asked for, and
/// <paramref name="FailedFilesTruncated"/> says whether there were more. <paramref name="Failed"/> is
/// always the exact count: a sample read as the total is how a folder with four hundred unindexed files
/// looks like one with ten.
/// </para>
/// </summary>
internal sealed record IndexCounts(
	Int64 Delivered,
	Int64 Pending,
	Int64 Failed,
	IReadOnlyList<FailedFile> FailedFiles,
	Boolean FailedFilesTruncated);

/// <summary>
/// Counts the index for a status endpoint (<c>SPEC-120</c>, <c>SPEC-121</c>).
///
/// <para>
/// <strong>It reads and never writes</strong>, on a read-only connection. A status endpoint is the one
/// thing that gets polled, and polling must not be able to perturb what it reports — a write-capable
/// connection here would contend for the same write lock the indexer needs to make the backlog it is
/// being asked about go down.
/// </para>
///
/// <para>
/// The two questions it answers come from different tables and are deliberately not derived from each
/// other. Delivered and pending are properties of a file's record — the delivery mark against the content
/// hash — while failed is a property of the queue, since a file given up on still has its record and
/// nothing in the record says so.
/// </para>
/// </summary>
internal sealed class IndexStatusReader
{
	private readonly String _databasePath;

	public IndexStatusReader(String databasePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

		this._databasePath = databasePath;
	}

	/// <summary>The counts, with at most <paramref name="maxFailedFiles"/> failed files named.</summary>
	public IndexCounts Read(Int32 maxFailedFiles)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(maxFailedFiles);

		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(this._databasePath);

		(Int64 delivered, Int64 pending) = ReadDelivery(connection);
		Int64 failed = ReadFailedCount(connection);
		(IReadOnlyList<FailedFile> sample, Boolean truncated) = ReadFailedSample(connection, maxFailedFiles);

		return new IndexCounts(delivered, pending, failed, sample, truncated);
	}

	/// <summary>
	/// Delivered files, and files whose content has moved on since what was delivered.
	///
	/// <para>
	/// A row with no mark at all counts as pending: nothing has been delivered for it, which is the same
	/// backlog as a file whose mark is stale. Only active rows are counted, because a row kept for a
	/// removal that has not been delivered describes a file the folder no longer has.
	/// </para>
	/// </summary>
	private static (Int64 Delivered, Int64 Pending) ReadDelivery(SqliteConnection connection)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = $"""
			SELECT
				COUNT(*) FILTER (WHERE last_synced_hash IS NOT NULL AND last_synced_hash = file_hash),
				COUNT(*) FILTER (WHERE last_synced_hash IS NULL OR last_synced_hash <> file_hash)
			FROM file_manifest
			WHERE status = '{FolderIndexStore.Active}';
			""";

		using SqliteDataReader reader = command.ExecuteReader();

		return reader.Read() ? (reader.GetInt64(0), reader.GetInt64(1)) : (0L, 0L);
	}

	/// <summary>
	/// How many <em>files</em> were given up on, not how many operations were: one file can fail an
	/// upsert and then a delete, and reporting two would overstate what is missing from the index.
	/// </summary>
	private static Int64 ReadFailedCount(SqliteConnection connection)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText =
			$"SELECT COUNT(DISTINCT file_path) FROM outbox WHERE status = {FolderIndexStore.Failed};";

		return (Int64)(command.ExecuteScalar() ?? 0L);
	}

	private static (IReadOnlyList<FailedFile> Sample, Boolean Truncated) ReadFailedSample(
		SqliteConnection connection,
		Int32 max)
	{
		if (max == 0)
		{
			return ([], false);
		}

		List<FailedFile> failures = [];

		using SqliteCommand command = connection.CreateCommand();

		// The most recent attempt per path, and one row more than asked for so the bound reports itself.
		command.CommandText = $"""
			SELECT file_path, error, attempts FROM outbox
			WHERE status = {FolderIndexStore.Failed}
			GROUP BY file_path
			HAVING id = MAX(id)
			ORDER BY file_path
			LIMIT $limit;
			""";
		command.Parameters.AddWithValue("$limit", max + 1);

		using (SqliteDataReader reader = command.ExecuteReader())
		{
			while (reader.Read())
			{
				failures.Add(new FailedFile(
					reader.GetString(0),
					reader.IsDBNull(1) ? null : reader.GetString(1),
					reader.GetInt32(2)));
			}
		}

		Boolean truncated = failures.Count > max;
		if (truncated)
		{
			failures.RemoveAt(failures.Count - 1);
		}

		return (failures, truncated);
	}
}
