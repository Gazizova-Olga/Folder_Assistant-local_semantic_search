using System.Text.Json;
using FluentAssertions;
using FolderAssistant.Persistence;
using Microsoft.Data.Sqlite;

namespace FolderAssistant.Tests;

/// <summary>
/// A folder indexed before vectors became blobs has a schema-2 database on disk, with its vectors
/// as JSON text. Bootstrap migrates it in place.
///
/// <para>
/// Re-encoding rather than rebuilding is the decision under test. The vectors are derived state
/// and *could* be rebuilt from the folder — but that means re-embedding every chunk, which for a
/// real model is the most expensive thing the system does, and it would strand the stored fit
/// artifact that has to stay consistent with the vectors produced under it.
/// </para>
/// </summary>
public sealed class VectorBlobMigrationTests
{
	[Fact]
	public void A_Schema_Two_Database_Is_Migrated_In_Place_And_Keeps_Its_Vectors()
	{
		using TempFolder folder = new();
		String databasePath = CreateSchemaTwoDatabase(folder, [0.5f, -0.25f, 1f]);

		new FolderDatabaseBootstrapper().EnsureInitialized(folder.Path, new PersistenceConfig());

		ReadScalar(databasePath, "SELECT version FROM schema_version;").Should().Be(3L);

		SqliteBlobVectorStoreReader reader = new();
		IReadOnlyList<StoredVector> stored = reader.ReadVectorsByModelVersion(databasePath, "m1");

		stored.Should().ContainSingle();
		stored[0].ChunkId.Should().Be("c1");
		stored[0].Vector.Should().Equal(0.5f, -0.25f, 1f);
	}

	/// <summary>The chunk and file rows the vectors hang off are untouched by the table rebuild.</summary>
	[Fact]
	public void The_Migration_Leaves_The_Rest_Of_The_Index_Alone()
	{
		using TempFolder folder = new();
		String databasePath = CreateSchemaTwoDatabase(folder, [1f, 0f, 0f]);

		new FolderDatabaseBootstrapper().EnsureInitialized(folder.Path, new PersistenceConfig());

		ReadScalar(databasePath, "SELECT COUNT(*) FROM file_manifest;").Should().Be(1L);
		ReadScalar(databasePath, "SELECT COUNT(*) FROM chunk_manifest;").Should().Be(1L);

		IReadOnlyDictionary<String, ChunkLocation> locations =
			new SqliteBlobVectorStoreReader().ReadChunkLocations(databasePath, ["c1"]);

		locations.Should().ContainKey("c1");
		locations["c1"].FilePath.Should().Be("notes.md");
	}

	/// <summary>Running bootstrap again finds no vector_json column and does nothing.</summary>
	[Fact]
	public void Migrating_An_Already_Migrated_Database_Is_A_No_Op()
	{
		using TempFolder folder = new();
		String databasePath = CreateSchemaTwoDatabase(folder, [0.25f, 0.5f, 0.75f]);

		new FolderDatabaseBootstrapper().EnsureInitialized(folder.Path, new PersistenceConfig());
		new FolderDatabaseBootstrapper().EnsureInitialized(folder.Path, new PersistenceConfig());

		IReadOnlyList<StoredVector> stored =
			new SqliteBlobVectorStoreReader().ReadVectorsByModelVersion(databasePath, "m1");

		stored.Should().ContainSingle();
		stored[0].Vector.Should().Equal(0.25f, 0.5f, 0.75f);
	}

	/// <summary>
	/// Builds the schema-2 layout by hand — a `vector_json TEXT` column and a seeded version of 2 —
	/// because the bootstrapper can no longer produce one.
	/// </summary>
	private static String CreateSchemaTwoDatabase(TempFolder folder, Single[] vector)
	{
		String metadata = folder.Combine(".folderassistant");
		Directory.CreateDirectory(metadata);

		String databasePath = Path.Combine(metadata, "manifest.db");

		using SqliteConnection connection = FolderDatabaseConnection.OpenCreate(databasePath);
		using SqliteCommand command = connection.CreateCommand();

		command.CommandText = """
			PRAGMA journal_mode = WAL;

			CREATE TABLE schema_version (id INTEGER PRIMARY KEY, version INTEGER NOT NULL);

			CREATE TABLE file_manifest (
				file_id TEXT PRIMARY KEY, file_path TEXT NOT NULL, file_hash TEXT NOT NULL,
				size_bytes INTEGER NOT NULL, modified_utc TEXT NOT NULL, file_type TEXT NOT NULL,
				indexed_utc TEXT NOT NULL);

			CREATE TABLE chunk_manifest (
				chunk_id TEXT PRIMARY KEY, file_id TEXT NOT NULL, chunk_index INTEGER NOT NULL,
				token_start INTEGER NOT NULL, token_end INTEGER NOT NULL, chunk_hash TEXT NOT NULL,
				model_version_id TEXT NOT NULL, updated_utc TEXT NOT NULL,
				FOREIGN KEY (file_id) REFERENCES file_manifest(file_id) ON DELETE CASCADE,
				UNIQUE (file_id, chunk_index));

			CREATE TABLE embedding_model_registry (
				model_version_id TEXT PRIMARY KEY, provider_type TEXT NOT NULL, model_name TEXT NOT NULL,
				vector_dimension INTEGER NOT NULL, is_active_for_write INTEGER NOT NULL DEFAULT 0,
				registered_utc TEXT NOT NULL);

			CREATE TABLE chunk_vector (
				chunk_id TEXT NOT NULL, model_version_id TEXT NOT NULL, vector_json TEXT NOT NULL,
				vector_dimension INTEGER NOT NULL, updated_utc TEXT NOT NULL,
				PRIMARY KEY (chunk_id, model_version_id),
				FOREIGN KEY (chunk_id) REFERENCES chunk_manifest(chunk_id) ON DELETE CASCADE,
				FOREIGN KEY (model_version_id) REFERENCES embedding_model_registry(model_version_id));

			CREATE TABLE embedding_fit_artifact (
				model_version_id TEXT PRIMARY KEY, artifact_json TEXT NOT NULL, created_utc TEXT NOT NULL,
				FOREIGN KEY (model_version_id) REFERENCES embedding_model_registry(model_version_id));

			CREATE INDEX idx_chunk_vector_model ON chunk_vector(model_version_id);

			INSERT INTO schema_version (id, version) VALUES (1, 2);

			INSERT INTO file_manifest VALUES
				('f1', 'notes.md', 'hash', 10, '2026-01-01T00:00:00Z', 'md', '2026-01-01T00:00:00Z');

			INSERT INTO chunk_manifest VALUES
				('c1', 'f1', 0, 0, 3, 'chash', 'm1', '2026-01-01T00:00:00Z');

			INSERT INTO embedding_model_registry VALUES
				('m1', 'local', 'model', 3, 1, '2026-01-01T00:00:00Z');
			""";
		command.ExecuteNonQuery();

		using SqliteCommand insertVector = connection.CreateCommand();
		insertVector.CommandText = """
			INSERT INTO chunk_vector (chunk_id, model_version_id, vector_json, vector_dimension, updated_utc)
			VALUES ('c1', 'm1', $json, $dimension, '2026-01-01T00:00:00Z');
			""";
		insertVector.Parameters.AddWithValue("$json", JsonSerializer.Serialize(vector));
		insertVector.Parameters.AddWithValue("$dimension", vector.Length);
		insertVector.ExecuteNonQuery();

		return databasePath;
	}

	private static Int64 ReadScalar(String databasePath, String sql)
	{
		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(databasePath);
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = sql;

		return (Int64)(command.ExecuteScalar() ?? 0L);
	}
}
