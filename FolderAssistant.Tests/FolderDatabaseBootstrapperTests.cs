using FluentAssertions;
using FolderAssistant.Persistence;
using Microsoft.Data.Sqlite;

namespace FolderAssistant.Tests;

public sealed class FolderDatabaseBootstrapperTests
{
	[Fact]
	public void It_Creates_The_Metadata_Folder_And_The_Database()
	{
		using TempFolder folder = new();

		DatabaseBootstrapResult result = BootstrapIn(folder.Path);

		Directory.Exists(Path.Combine(folder.Path, ".folderassistant")).Should().BeTrue();
		File.Exists(result.DatabasePath).Should().BeTrue();
		result.Created.Should().BeTrue();
	}

	[Fact]
	public void Reusing_An_Existing_Database_Reports_That_It_Was_Not_Created()
	{
		using TempFolder folder = new();

		BootstrapIn(folder.Path);

		BootstrapIn(folder.Path).Created.Should().BeFalse();
	}

	[Fact]
	public void The_Database_Is_In_Write_Ahead_Logging_Mode()
	{
		using TempFolder folder = new();

		ReadScalar(BootstrapIn(folder.Path).DatabasePath, "PRAGMA journal_mode;").Should().Be("wal");
	}

	[Fact]
	public void Every_Table_And_Index_Is_Created()
	{
		using TempFolder folder = new();

		DatabaseBootstrapResult result = BootstrapIn(folder.Path);

		ObjectNames(result.DatabasePath, "table").Should().Contain([
			"schema_version",
			"file_manifest",
			"chunk_manifest",
			"embedding_model_registry",
			"chunk_vector",
			"embedding_fit_artifact",
		]);

		ObjectNames(result.DatabasePath, "index").Should().Contain([
			"idx_file_manifest_path",
			"idx_chunk_manifest_file",
			"idx_chunk_vector_model",
		]);
	}

	[Fact]
	public void The_Schema_Version_Is_Seeded_Once()
	{
		using TempFolder folder = new();

		DatabaseBootstrapResult result = BootstrapIn(folder.Path);
		BootstrapIn(folder.Path);

		ReadScalar(result.DatabasePath, "SELECT COUNT(*) FROM schema_version;").Should().Be(1L);
		ReadScalar(result.DatabasePath, "SELECT version FROM schema_version;").Should().Be(2L);
	}

	/// <summary>
	/// Callers starting together all find the file absent, so the presence of the file cannot be what
	/// <c>Created</c> reports. Exactly one of them inserts the version row, and that is the one.
	/// </summary>
	[Fact]
	public async Task Only_One_Of_Several_Concurrent_Bootstraps_Reports_Creating_The_Database()
	{
		using TempFolder folder = new();

		DatabaseBootstrapResult[] results = await Task.WhenAll(
			Enumerable.Range(0, 8).Select(_ => Task.Run(() => BootstrapIn(folder.Path))));

		results.Select(static r => r.DatabasePath).Distinct().Should().ContainSingle();
		results.Count(static r => r.Created).Should().Be(1);
		ReadScalar(results[0].DatabasePath, "SELECT COUNT(*) FROM schema_version;").Should().Be(1L);
	}

	/// <summary>
	/// A run that fails partway has to leave nothing behind. Every statement is <c>IF NOT EXISTS</c>,
	/// so a half-created schema reads as complete on the next run and the missing tables are never
	/// made — the failure would be permanent and silent.
	///
	/// <para>
	/// The failure is staged by taking the name the last index wants, which makes the DDL fail after
	/// all five tables have been created and so leaves the transaction something to undo.
	/// </para>
	/// </summary>
	[Fact]
	public void A_Failure_Partway_Through_The_Schema_Leaves_Nothing_Behind()
	{
		using TempFolder folder = new();

		String databasePath = Path.Combine(folder.Path, ".folderassistant", "manifest.db");
		Directory.CreateDirectory(Path.Combine(folder.Path, ".folderassistant"));

		using (SqliteConnection staged = Connect(databasePath))
		{
			Execute(staged, "CREATE TABLE idx_file_manifest_path (id INTEGER PRIMARY KEY);");
		}

		FluentActions.Invoking(() => BootstrapIn(folder.Path)).Should().Throw<SqliteException>();

		ObjectNames(databasePath, "table").Should()
			.Contain("idx_file_manifest_path")
			.And.NotContain([
				"schema_version",
				"file_manifest",
				"chunk_manifest",
				"embedding_model_registry",
				"chunk_vector",
			]);
	}

	/// <summary>
	/// The reason the schema is created whole: a second embedding model has to be able to store its
	/// vectors alongside the first, without migrating what is already there.
	/// </summary>
	[Fact]
	public void One_Chunk_Can_Hold_A_Vector_From_Each_Of_Two_Models()
	{
		using TempFolder folder = new();

		using SqliteConnection connection = Connect(BootstrapIn(folder.Path).DatabasePath);
		SeedOneChunk(connection);

		Execute(connection, """
			INSERT INTO embedding_model_registry
				(model_version_id, provider_type, model_name, vector_dimension, activated_utc)
			VALUES ('m1', 'local', 'first', 3, '2026-01-01T00:00:00Z'),
			       ('m2', 'local', 'second', 3, '2026-01-01T00:00:00Z');

			INSERT INTO chunk_vector (chunk_id, model_version_id, vector_json, vector_dimension, updated_utc)
			VALUES ('c1', 'm1', '[1,0,0]', 3, '2026-01-01T00:00:00Z'),
			       ('c1', 'm2', '[0,1,0]', 3, '2026-01-01T00:00:00Z');
			""");

		Scalar(connection, "SELECT COUNT(*) FROM chunk_vector WHERE chunk_id = 'c1';").Should().Be(2L);
	}

	/// <summary>
	/// Deleting a file takes its chunks and their vectors with it — a property of the schema, not of
	/// any one connection. Foreign keys are per-connection, so this test enables them on its own
	/// connection; what it pins is that the cascade is declared.
	/// </summary>
	[Fact]
	public void Deleting_A_File_Cascades_To_Its_Chunks_And_Vectors()
	{
		using TempFolder folder = new();

		using SqliteConnection connection = Connect(BootstrapIn(folder.Path).DatabasePath);
		SeedOneChunk(connection);

		Execute(connection, """
			INSERT INTO embedding_model_registry
				(model_version_id, provider_type, model_name, vector_dimension, activated_utc)
			VALUES ('m1', 'local', 'first', 3, '2026-01-01T00:00:00Z');

			INSERT INTO chunk_vector (chunk_id, model_version_id, vector_json, vector_dimension, updated_utc)
			VALUES ('c1', 'm1', '[1,0,0]', 3, '2026-01-01T00:00:00Z');
			""");

		Execute(connection, "DELETE FROM file_manifest WHERE file_id = 'f1';");

		Scalar(connection, "SELECT COUNT(*) FROM chunk_manifest;").Should().Be(0L);
		Scalar(connection, "SELECT COUNT(*) FROM chunk_vector;").Should().Be(0L);
	}

	[Fact]
	public void A_Configured_Metadata_Folder_Name_Is_Honoured()
	{
		using TempFolder folder = new();

		DatabaseBootstrapResult result = BootstrapIn(folder.Path, metadataFolderName: "_custom");

		result.DatabasePath.Should().StartWith(Path.Combine(folder.Path, "_custom"));
		Directory.Exists(Path.Combine(folder.Path, "_custom")).Should().BeTrue();
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void A_Blank_Analyzed_Folder_Is_Refused(String path)
	{
		FluentActions.Invoking(() => new FolderDatabaseBootstrapper().EnsureInitialized(path, new PersistenceConfig()))
			.Should().Throw<ArgumentException>();
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void A_Blank_Metadata_Folder_Name_Is_Refused(String name)
	{
		using TempFolder folder = new();

		FluentActions.Invoking(() => BootstrapIn(folder.Path, metadataFolderName: name))
			.Should().Throw<ArgumentException>();
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void A_Blank_Database_File_Name_Is_Refused(String name)
	{
		using TempFolder folder = new();

		FluentActions.Invoking(() => BootstrapIn(folder.Path, databaseFileName: name))
			.Should().Throw<ArgumentException>();
	}

	private static DatabaseBootstrapResult BootstrapIn(
		String analyzedFolder,
		String? metadataFolderName = null,
		String? databaseFileName = null)
	{
		PersistenceConfig config = new();

		if (metadataFolderName is not null)
		{
			config = config with { MetadataFolderName = metadataFolderName };
		}

		if (databaseFileName is not null)
		{
			config = config with { DatabaseFileName = databaseFileName };
		}

		return new FolderDatabaseBootstrapper().EnsureInitialized(analyzedFolder, config);
	}

	private static SqliteConnection Connect(String databasePath)
	{
		SqliteConnection connection = new($"Data Source={databasePath}");
		connection.Open();

		using SqliteCommand pragma = connection.CreateCommand();
		pragma.CommandText = "PRAGMA foreign_keys = ON;";
		pragma.ExecuteNonQuery();

		return connection;
	}

	private static void SeedOneChunk(SqliteConnection connection)
		=> Execute(connection, """
			INSERT INTO file_manifest (file_id, file_path, file_hash, size_bytes, modified_utc, updated_utc)
			VALUES ('f1', 'notes/a.md', 'hash', 10, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');

			INSERT INTO chunk_manifest
				(chunk_id, file_id, chunk_index, token_start, token_end, chunk_hash, model_version_id, updated_utc)
			VALUES ('c1', 'f1', 0, 0, 5, 'chunkhash', 'm1', '2026-01-01T00:00:00Z');
			""");

	private static void Execute(SqliteConnection connection, String sql)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = sql;
		command.ExecuteNonQuery();
	}

	private static Object Scalar(SqliteConnection connection, String sql)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = sql;

		return command.ExecuteScalar()!;
	}

	private static Object ReadScalar(String databasePath, String sql)
	{
		using SqliteConnection connection = Connect(databasePath);

		return Scalar(connection, sql);
	}

	private static List<String> ObjectNames(String databasePath, String type)
	{
		using SqliteConnection connection = Connect(databasePath);
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type;";
		command.Parameters.AddWithValue("$type", type);

		List<String> names = [];
		using SqliteDataReader reader = command.ExecuteReader();
		while (reader.Read())
		{
			names.Add(reader.GetString(0));
		}

		return names;
	}
}
