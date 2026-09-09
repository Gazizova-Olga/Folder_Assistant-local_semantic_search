using Microsoft.Data.Sqlite;

namespace FolderAssistant.Persistence;

/// <summary>
/// The one place the folder database is opened.
///
/// <para>
/// The settings below are what make concurrent access safe, and they are per-connection, so every
/// caller has to apply all of them. Spread across call sites that is a rule nobody can hold: the
/// shared-cache flag reached all four of them by someone copying a working connection string, and
/// the writer ran with no busy timeout at all because only the bootstrapper had ever set one.
/// </para>
/// </summary>
internal static class FolderDatabaseConnection
{
	/// <summary>How long a connection waits for a lock before giving up.</summary>
	public const Int32 BusyTimeoutMilliseconds = 5000;

	/// <summary>Opens for reading. Safe alongside the indexing writer, under WAL.</summary>
	public static SqliteConnection OpenRead(String databasePath, Boolean withVectorExtension = false)
		=> Open(databasePath, SqliteOpenMode.ReadOnly, withVectorExtension);

	/// <summary>Opens for writing. Writers are expected to be serialized.</summary>
	public static SqliteConnection OpenWrite(String databasePath, Boolean withVectorExtension = false)
		=> Open(databasePath, SqliteOpenMode.ReadWrite, withVectorExtension);

	/// <summary>Opens for writing, creating the database file if it is not there yet.</summary>
	public static SqliteConnection OpenCreate(String databasePath, Boolean withVectorExtension = false)
		=> Open(databasePath, SqliteOpenMode.ReadWriteCreate, withVectorExtension);

	/// <summary>
	/// <paramref name="withVectorExtension"/> loads the native sqlite-vec extension onto the connection.
	///
	/// <para>
	/// Opt-in rather than always on: the extension is a per-RID native binary that does not exist on
	/// every platform, and a database written by the default backend never needs it. Only the
	/// sqlite-vec store asks, and it asks through <c>IVectorStoreWriter.RequiresVectorExtension</c> so
	/// that no caller has to remember to.
	/// </para>
	/// </summary>
	private static SqliteConnection Open(String databasePath, SqliteOpenMode mode, Boolean withVectorExtension)
	{
		// No Cache=Shared, deliberately. It is only needed to share an *in-memory* database between
		// connections. On a file-backed one it makes every connection in the process share a single
		// cache, and concurrent use then faults inside sqlite3_prepare_v2 when the GC finalizes a
		// handle while another connection is preparing a statement. It also turns contention into
		// SQLITE_LOCKED, which the busy handler is never invoked for — so it silently defeats the
		// busy_timeout set below. WAL is what actually lets the indexer write while retrieval reads.
		SqliteConnection connection = new(new SqliteConnectionStringBuilder
		{
			DataSource = databasePath,
			Mode = mode,
		}.ToString());

		connection.Open();

		// Both are per-connection and neither is persisted in the file, unlike journal_mode. A
		// connection that skips foreign_keys gets no cascading deletes, silently.
		using (SqliteCommand pragma = connection.CreateCommand())
		{
			pragma.CommandText =
				$"PRAGMA foreign_keys = ON; PRAGMA busy_timeout = {BusyTimeoutMilliseconds};";
			pragma.ExecuteNonQuery();
		}

		if (withVectorExtension)
		{
			SqliteVecExtension.LoadOnto(connection);
		}

		return connection;
	}
}
