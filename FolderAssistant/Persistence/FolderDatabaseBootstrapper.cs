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
/// </summary>
internal sealed class FolderDatabaseBootstrapper : IFolderDatabaseBootstrapper
{
	private const Int32 SchemaVersion = 1;

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
		Boolean created = !File.Exists(databasePath);

		using SqliteConnection connection = new(new SqliteConnectionStringBuilder
		{
			DataSource = databasePath,
			Mode = SqliteOpenMode.ReadWriteCreate,
			Cache = SqliteCacheMode.Shared,
		}.ToString());

		connection.Open();

		using (SqliteCommand pragma = connection.CreateCommand())
		{
			pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;";
			pragma.ExecuteNonQuery();
		}

		using SqliteCommand schema = connection.CreateCommand();
		schema.CommandText = $"""
			CREATE TABLE IF NOT EXISTS schema_version (
				id      INTEGER PRIMARY KEY CHECK (id = 1),
				version INTEGER NOT NULL
			);

			INSERT INTO schema_version (id, version)
			SELECT 1, {SchemaVersion}
			WHERE NOT EXISTS (SELECT 1 FROM schema_version WHERE id = 1);

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
			CREATE INDEX IF NOT EXISTS idx_chunk_vector_model ON chunk_vector(model_version_id);
			""";
		schema.ExecuteNonQuery();

		return new DatabaseBootstrapResult(databasePath, created);
	}
}
