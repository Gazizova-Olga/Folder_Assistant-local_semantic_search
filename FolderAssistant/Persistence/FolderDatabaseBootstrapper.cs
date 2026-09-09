using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FolderAssistant.Persistence;

/// <summary>Where the database is, and whether this call is what brought it into existence.</summary>
internal sealed record DatabaseBootstrapResult(String DatabasePath, Boolean Created);

/// <summary>Brings the folder-scoped database into a usable state before anything reads from it.</summary>
internal interface IFolderDatabaseBootstrapper
{
	/// <summary>Ensures the metadata folder, the database and its schema exist. Idempotent.</summary>
	DatabaseBootstrapResult EnsureInitialized(String analyzedFolderPath, PersistenceConfig config);
}

/// <summary>
/// Creates the metadata folder and the database inside it, and ensures the schema on every run.
///
/// <para>
/// The schema is created whole rather than grown a table at a time, because one of its invariants
/// cannot be retrofitted: the manifest, chunk and vector tables have to stay compatible when the
/// embedding implementation changes between runs. That is what <c>embedding_model_registry</c> and
/// the composite key on <c>chunk_vector</c> are for — a second embedder has to be able to coexist
/// with the first, and a schema keyed for one embedder would need every stored vector migrated
/// before a second could be introduced.
/// </para>
///
/// <para>
/// Every statement is <c>IF NOT EXISTS</c>, so this runs on every start, and versioned migrations
/// can hang off <c>schema_version</c> later.
/// </para>
///
/// <para>
/// The schema and the version row go in under one transaction, so a run that fails partway leaves
/// the database as it found it rather than half-built — a half-built database still satisfies every
/// <c>IF NOT EXISTS</c> on the next run, so the missing tables would never be created.
/// </para>
///
/// <para>
/// <c>Created</c> reports whether this call inserted the <c>schema_version</c> row, not whether the
/// file was absent beforehand. Two callers starting together both find no file and would both claim
/// to have created it; only one of them inserts.
/// </para>
/// </summary>
internal sealed class FolderDatabaseBootstrapper : IFolderDatabaseBootstrapper
{
	// Version 2 adds embedding_fit_artifact. An existing version 1 database picks the table up
	// through the idempotent CREATE below; what changes here is only the value seeded into a
	// database created from now on.
	private const Int32 SchemaVersion = 3;

	/// <summary>Ensures the metadata folder, the database and its schema exist. Idempotent.</summary>
	public DatabaseBootstrapResult EnsureInitialized(String analyzedFolderPath, PersistenceConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);

		if (String.IsNullOrWhiteSpace(analyzedFolderPath))
		{
			throw new ArgumentException("Analyzed folder path must be provided.", nameof(analyzedFolderPath));
		}

		if (String.IsNullOrWhiteSpace(config.MetadataFolderName))
		{
			throw new ArgumentException("Metadata folder name must be provided.", nameof(config));
		}

		if (String.IsNullOrWhiteSpace(config.DatabaseFileName))
		{
			throw new ArgumentException("Database file name must be provided.", nameof(config));
		}

		String rootPath = Path.GetFullPath(analyzedFolderPath);
		String metadataPath = Path.Combine(rootPath, config.MetadataFolderName);
		Directory.CreateDirectory(metadataPath);

		String databasePath = Path.Combine(metadataPath, config.DatabaseFileName);

		using SqliteConnection connection = FolderDatabaseConnection.OpenCreate(databasePath);

		using (SqliteCommand pragma = connection.CreateCommand())
		{
			// journal_mode is persisted in the database file, so unlike the per-connection PRAGMAs
			// the connection factory sets, it only has to be written once — here. WAL is what lets
			// retrieval read while the indexer writes.
			pragma.CommandText = "PRAGMA journal_mode = WAL;";
			pragma.ExecuteNonQuery();
		}

		using SqliteTransaction transaction = connection.BeginTransaction();

		using SqliteCommand schema = connection.CreateCommand();
		schema.Transaction = transaction;
		schema.CommandText = """
			CREATE TABLE IF NOT EXISTS schema_version (
				id      INTEGER PRIMARY KEY CHECK (id = 1),
				version INTEGER NOT NULL
			);

			CREATE TABLE IF NOT EXISTS file_manifest (
				file_id      TEXT PRIMARY KEY,
				file_path    TEXT NOT NULL UNIQUE,
				file_hash    TEXT NOT NULL,
				size_bytes   INTEGER NOT NULL,
				modified_utc TEXT NOT NULL,
				status       TEXT NOT NULL DEFAULT 'active',
				updated_utc  TEXT NOT NULL
			);

			CREATE TABLE IF NOT EXISTS chunk_manifest (
				chunk_id         TEXT PRIMARY KEY,
				file_id          TEXT NOT NULL,
				chunk_index      INTEGER NOT NULL,
				token_start      INTEGER NOT NULL,
				token_end        INTEGER NOT NULL,
				chunk_hash       TEXT NOT NULL,
				model_version_id TEXT NOT NULL,
				updated_utc      TEXT NOT NULL,
				FOREIGN KEY (file_id) REFERENCES file_manifest(file_id) ON DELETE CASCADE,
				UNIQUE (file_id, chunk_index)
			);

			CREATE TABLE IF NOT EXISTS embedding_model_registry (
				model_version_id    TEXT PRIMARY KEY,
				provider_type       TEXT NOT NULL,
				model_name          TEXT NOT NULL,
				vector_dimension    INTEGER NOT NULL,
				distance_metric     TEXT NOT NULL DEFAULT 'cosine',
				is_active_for_write INTEGER NOT NULL DEFAULT 0,
				activated_utc       TEXT NOT NULL
			);

			CREATE TABLE IF NOT EXISTS chunk_vector (
				chunk_id         TEXT NOT NULL,
				model_version_id TEXT NOT NULL,
				vector           BLOB NOT NULL,
				vector_dimension INTEGER NOT NULL,
				updated_utc      TEXT NOT NULL,
				PRIMARY KEY (chunk_id, model_version_id),
				FOREIGN KEY (chunk_id) REFERENCES chunk_manifest(chunk_id) ON DELETE CASCADE,
				FOREIGN KEY (model_version_id) REFERENCES embedding_model_registry(model_version_id)
			);

			CREATE INDEX IF NOT EXISTS idx_file_manifest_path ON file_manifest(file_path);
			CREATE INDEX IF NOT EXISTS idx_chunk_manifest_file ON chunk_manifest(file_id);
			CREATE TABLE IF NOT EXISTS embedding_fit_artifact (
				model_version_id TEXT PRIMARY KEY,
				artifact_json    TEXT NOT NULL,
				created_utc      TEXT NOT NULL,
				FOREIGN KEY (model_version_id) REFERENCES embedding_model_registry(model_version_id) ON DELETE CASCADE
			);

			CREATE INDEX IF NOT EXISTS idx_chunk_vector_model ON chunk_vector(model_version_id);
			""";
		schema.ExecuteNonQuery();

		using SqliteCommand seed = connection.CreateCommand();
		seed.Transaction = transaction;
		seed.CommandText = $"""
			INSERT INTO schema_version (id, version)
			SELECT 1, {SchemaVersion}
			WHERE NOT EXISTS (SELECT 1 FROM schema_version WHERE id = 1);
			""";

		Boolean created = seed.ExecuteNonQuery() > 0;

		MigrateJsonVectorsToBlobs(connection, transaction);

		transaction.Commit();

		return new DatabaseBootstrapResult(databasePath, created);
	}

	/// <summary>
	/// Converts a schema-2 database, whose vectors are JSON text in a <c>vector_json</c> column, to
	/// the schema-3 packed-blob layout.
	///
	/// <para>
	/// The vectors are re-encoded rather than discarded. They are derived state and could be rebuilt
	/// from the folder — but rebuilding means re-embedding every chunk, which for a real embedding
	/// model is the single most expensive thing the system does. It would also strand the stored fit
	/// artifact, which has to stay consistent with the vectors produced under it.
	/// </para>
	///
	/// <para>
	/// New tables arrive through the idempotent <c>CREATE TABLE IF NOT EXISTS</c> statements above.
	/// A changed column *type* does not, which is why this exists at all.
	/// </para>
	/// </summary>
	private static void MigrateJsonVectorsToBlobs(SqliteConnection connection, SqliteTransaction transaction)
	{
		if (!HasColumn(connection, transaction, "chunk_vector", "vector_json"))
		{
			return;
		}

		List<(String ChunkId, String ModelVersionId, Single[] Vector, Int32 Dimension, String UpdatedUtc)> rows = [];

		using (SqliteCommand read = connection.CreateCommand())
		{
			read.Transaction = transaction;
			read.CommandText =
				"SELECT chunk_id, model_version_id, vector_json, vector_dimension, updated_utc FROM chunk_vector;";

			using SqliteDataReader reader = read.ExecuteReader();

			while (reader.Read())
			{
				Single[] vector = JsonSerializer.Deserialize<Single[]>(reader.GetString(2)) ?? [];
				rows.Add((reader.GetString(0), reader.GetString(1), vector, reader.GetInt32(3), reader.GetString(4)));
			}
		}

		// SQLite cannot change a column's type in place, so the table is rebuilt. Inside the bootstrap
		// transaction, deliberately: a half-migrated chunk_vector is indistinguishable from a corrupt
		// one, and there would be no way to tell which had happened.
		using (SqliteCommand rebuild = connection.CreateCommand())
		{
			rebuild.Transaction = transaction;
			rebuild.CommandText = """
				DROP TABLE chunk_vector;

				CREATE TABLE chunk_vector (
					chunk_id         TEXT NOT NULL,
					model_version_id TEXT NOT NULL,
					vector           BLOB NOT NULL,
					vector_dimension INTEGER NOT NULL,
					updated_utc      TEXT NOT NULL,
					PRIMARY KEY (chunk_id, model_version_id),
					FOREIGN KEY (chunk_id) REFERENCES chunk_manifest(chunk_id) ON DELETE CASCADE,
					FOREIGN KEY (model_version_id) REFERENCES embedding_model_registry(model_version_id)
				);

				CREATE INDEX IF NOT EXISTS idx_chunk_vector_model ON chunk_vector(model_version_id);
				""";
			rebuild.ExecuteNonQuery();
		}

		using (SqliteCommand insert = connection.CreateCommand())
		{
			insert.Transaction = transaction;
			insert.CommandText = """
				INSERT INTO chunk_vector (chunk_id, model_version_id, vector, vector_dimension, updated_utc)
				VALUES ($chunkId, $modelVersionId, $vector, $dimension, $updated);
				""";

			SqliteParameter chunkId = insert.Parameters.Add("$chunkId", SqliteType.Text);
			SqliteParameter modelVersionId = insert.Parameters.Add("$modelVersionId", SqliteType.Text);
			SqliteParameter vector = insert.Parameters.Add("$vector", SqliteType.Blob);
			SqliteParameter dimension = insert.Parameters.Add("$dimension", SqliteType.Integer);
			SqliteParameter updated = insert.Parameters.Add("$updated", SqliteType.Text);

			foreach ((String id, String model, Single[] values, Int32 dim, String utc) in rows)
			{
				chunkId.Value = id;
				modelVersionId.Value = model;
				vector.Value = VectorBlob.Pack(values);
				dimension.Value = dim;
				updated.Value = utc;
				insert.ExecuteNonQuery();
			}
		}

		using SqliteCommand bump = connection.CreateCommand();
		bump.Transaction = transaction;
		bump.CommandText = $"UPDATE schema_version SET version = {SchemaVersion} WHERE id = 1;";
		bump.ExecuteNonQuery();
	}

	private static Boolean HasColumn(
		SqliteConnection connection,
		SqliteTransaction transaction,
		String table,
		String column)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column;";
		command.Parameters.AddWithValue("$column", column);

		return (Int64)(command.ExecuteScalar() ?? 0L) > 0;
	}
}
