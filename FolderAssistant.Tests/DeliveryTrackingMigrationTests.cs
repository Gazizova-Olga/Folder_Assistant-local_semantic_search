using FluentAssertions;
using FolderAssistant.Persistence;
using Microsoft.Data.Sqlite;

namespace FolderAssistant.Tests;

/// <summary>
/// A folder indexed before deliveries were tracked has a database with no room to record them: no
/// queue of outstanding work, and no column saying what content was last handed to the embedder.
/// Bootstrap adds both in place.
///
/// <para>
/// The added columns are nullable, and that is the point rather than a convenience. An existing row
/// has no creation time recorded and has had nothing delivered under this bookkeeping; a default
/// would state something about it that nobody knows, and the first thing to read it would believe
/// the statement.
/// </para>
/// </summary>
public sealed class DeliveryTrackingMigrationTests
{
	[Fact]
	public void A_Database_From_Before_Deliveries_Were_Tracked_Gains_What_It_Lacks()
	{
		using TempFolder folder = new();
		String databasePath = CreateDatabaseWithoutDeliveryTracking(folder);

		new FolderDatabaseBootstrapper().EnsureInitialized(folder.Path, new PersistenceConfig());

		Columns(databasePath, "file_manifest").Should().Contain(["created_utc", "last_synced_hash"]);
		Tables(databasePath).Should().Contain("outbox");
		ReadScalar(databasePath, "SELECT version FROM schema_version;").Should().Be(4L);
	}

	/// <summary>
	/// The rows already there are the index. A migration that rebuilt the table and lost them would
	/// silently cost a full re-embed of the folder, which is the most expensive thing this system
	/// does — and it would look like a first run rather than like a fault.
	/// </summary>
	[Fact]
	public void The_Migration_Keeps_The_Records_It_Found()
	{
		using TempFolder folder = new();
		String databasePath = CreateDatabaseWithoutDeliveryTracking(folder);

		new FolderDatabaseBootstrapper().EnsureInitialized(folder.Path, new PersistenceConfig());

		ReadScalar(databasePath, "SELECT COUNT(*) FROM file_manifest;").Should().Be(1L);
		ReadScalar(databasePath, "SELECT file_hash FROM file_manifest WHERE file_id = 'f1';").Should().Be("h1");

		// Unknown rather than assumed: nothing recorded either of these for a row written before the
		// columns existed.
		ReadScalar(databasePath, "SELECT created_utc FROM file_manifest WHERE file_id = 'f1';")
			.Should().Be(DBNull.Value);
		ReadScalar(databasePath, "SELECT last_synced_hash FROM file_manifest WHERE file_id = 'f1';")
			.Should().Be(DBNull.Value);
	}

	/// <summary>
	/// Bootstrap runs on every start, and the queue holds work that has not been delivered. Recreating
	/// the table would discard exactly the operations nothing else remembers.
	/// </summary>
	[Fact]
	public void A_Second_Bootstrap_Leaves_Queued_Work_Alone()
	{
		using TempFolder folder = new();

		DatabaseBootstrapResult first = new FolderDatabaseBootstrapper()
			.EnsureInitialized(folder.Path, new PersistenceConfig());

		using (SqliteConnection connection = FolderDatabaseConnection.OpenWrite(first.DatabasePath))
		{
			using SqliteCommand queue = connection.CreateCommand();
			queue.CommandText = """
				INSERT INTO outbox (file_path, op_type, status, attempts, created_utc, next_attempt_utc)
				VALUES ('notes.md', 0, 0, 0, '2026-09-12T00:00:00Z', '2026-09-12T00:00:00Z');
				""";
			queue.ExecuteNonQuery();
		}

		new FolderDatabaseBootstrapper().EnsureInitialized(folder.Path, new PersistenceConfig());

		ReadScalar(first.DatabasePath, "SELECT COUNT(*) FROM outbox;").Should().Be(1L);
		ReadScalar(first.DatabasePath, "SELECT file_path FROM outbox;").Should().Be("notes.md");
	}

	/// <summary>Running bootstrap twice finds the columns present and does nothing.</summary>
	[Fact]
	public void Migrating_An_Already_Migrated_Database_Is_A_No_Op()
	{
		using TempFolder folder = new();
		String databasePath = CreateDatabaseWithoutDeliveryTracking(folder);

		new FolderDatabaseBootstrapper().EnsureInitialized(folder.Path, new PersistenceConfig());

		Action again = () => new FolderDatabaseBootstrapper().EnsureInitialized(folder.Path, new PersistenceConfig());

		again.Should().NotThrow();
		Columns(databasePath, "file_manifest").Should().ContainSingle(column => column == "created_utc");
	}

	/// <summary>
	/// The shape a database had before any of this existed: a manifest with no delivery columns, and
	/// no queue at all.
	/// </summary>
	private static String CreateDatabaseWithoutDeliveryTracking(TempFolder folder)
	{
		PersistenceConfig config = new();
		String metadataFolder = Path.Combine(folder.Path, config.MetadataFolderName);

		Directory.CreateDirectory(metadataFolder);

		String databasePath = Path.Combine(metadataFolder, config.DatabaseFileName);

		using SqliteConnection connection = FolderDatabaseConnection.OpenCreate(databasePath);
		using SqliteCommand create = connection.CreateCommand();

		create.CommandText = """
			CREATE TABLE schema_version (
				id      INTEGER PRIMARY KEY CHECK (id = 1),
				version INTEGER NOT NULL
			);

			CREATE TABLE file_manifest (
				file_id      TEXT PRIMARY KEY,
				file_path    TEXT NOT NULL UNIQUE,
				file_hash    TEXT NOT NULL,
				size_bytes   INTEGER NOT NULL,
				modified_utc TEXT NOT NULL,
				status       TEXT NOT NULL DEFAULT 'active',
				updated_utc  TEXT NOT NULL
			);

			INSERT INTO schema_version (id, version) VALUES (1, 3);

			INSERT INTO file_manifest (file_id, file_path, file_hash, size_bytes, modified_utc, updated_utc)
			VALUES ('f1', 'notes.md', 'h1', 12, '2026-09-01T00:00:00Z', '2026-09-01T00:00:00Z');
			""";
		create.ExecuteNonQuery();

		return databasePath;
	}

	private static List<String> Columns(String databasePath, String table)
	{
		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(databasePath);
		using SqliteCommand command = connection.CreateCommand();

		command.CommandText = $"SELECT name FROM pragma_table_info('{table}');";

		List<String> names = [];
		using SqliteDataReader reader = command.ExecuteReader();

		while (reader.Read())
		{
			names.Add(reader.GetString(0));
		}

		return names;
	}

	private static List<String> Tables(String databasePath)
	{
		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(databasePath);
		using SqliteCommand command = connection.CreateCommand();

		command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";

		List<String> names = [];
		using SqliteDataReader reader = command.ExecuteReader();

		while (reader.Read())
		{
			names.Add(reader.GetString(0));
		}

		return names;
	}

	private static Object ReadScalar(String databasePath, String sql)
	{
		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(databasePath);
		using SqliteCommand command = connection.CreateCommand();

		command.CommandText = sql;

		return command.ExecuteScalar()!;
	}
}
