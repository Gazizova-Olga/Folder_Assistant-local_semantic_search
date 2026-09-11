using Microsoft.Data.Sqlite;

namespace FolderAssistant.Persistence;

/// <summary>
/// Maintenance the folder database needs once a bulk write has finished.
///
/// <para>
/// Under WAL — which is what lets retrieval read while the indexer writes — a committed write lives in the
/// log until a checkpoint copies it back into the database. SQLite's automatic checkpoint runs on a page
/// threshold while writing and never shrinks the log file, so after a bulk index the folder can be left
/// carrying a log sized to the burst it just absorbed, until some unrelated later writer happens to reclaim
/// it. A checkpoint is worth taking exactly when the writing stops; taken per write, it would block readers
/// over and over to reclaim the same space.
/// </para>
/// </summary>
internal static class FolderDatabaseMaintenance
{
	private const String TruncatingCheckpoint = "PRAGMA wal_checkpoint(TRUNCATE);";

	/// <summary>
	/// Requests a truncating checkpoint. Best effort by design: while a reader still holds an older snapshot,
	/// SQLite reports the checkpoint as busy in its result row instead of failing, and a later quiet moment
	/// takes it. It reclaims disk and changes nothing that is stored or retrievable.
	/// </summary>
	public static void Checkpoint(String databasePath)
	{
		using SqliteConnection connection = FolderDatabaseConnection.OpenWrite(databasePath);
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = TruncatingCheckpoint;
		command.ExecuteNonQuery();
	}
}
