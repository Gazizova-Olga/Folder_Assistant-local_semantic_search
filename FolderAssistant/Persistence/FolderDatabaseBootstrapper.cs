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
	private const Int32 SchemaVersion = 2;

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

		using SqliteConnection connection = new(new SqliteConnectionStringBuilder
		{
			DataSource = databasePath,
			Mode = SqliteOpenMode.ReadWriteCreate,
			Cache = SqliteCacheMode.Shared,
		}.ToString());

		connection.Open();

		using (SqliteCommand pragma = connection.CreateCommand())
		{
			// busy_timeout makes a connection wait for a held lock instead of failing at once. No test
			// here demonstrates it: removing it leaves eight concurrent bootstraps passing, five runs
			// out of five — so it stands as a guard against contention these tests do not produce.
			pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
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
				vector_json      TEXT NOT NULL,
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

		transaction.Commit();

		return new DatabaseBootstrapResult(databasePath, created);
	}
}
