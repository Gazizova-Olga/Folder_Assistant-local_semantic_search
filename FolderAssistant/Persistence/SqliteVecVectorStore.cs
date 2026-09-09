using System.Text;
using Microsoft.Data.Sqlite;

namespace FolderAssistant.Persistence;

/// <summary>
/// Naming and discovery for the <c>vec0</c> virtual tables, shared by the writer and the reader.
///
/// <para>
/// <b>One table per model version.</b> A <c>vec0</c> table fixes its vector dimension at creation, so a
/// single table cannot hold two models of different dimensions — and models coexisting is a requirement
/// rather than an accident. Each model version therefore gets its own table, which turns out to be a
/// happy constraint: "a query vector is compared only against vectors sharing its model version" stops
/// being a runtime check and becomes impossible to violate, because the other model's vectors are not
/// in the table being searched.
/// </para>
///
/// <para>
/// <b>No foreign keys.</b> A virtual table cannot be the target of one, so nothing cascades here. This
/// is the backend that an explicit <c>DeleteVectors</c> on the contract exists for.
/// </para>
/// </summary>
internal static class SqliteVecTable
{
	private const String Prefix = "vec_chunk_vector__";

	/// <summary>
	/// The table name for a model version. The id is sanitised because it lands in DDL, where it cannot
	/// be a bound parameter — SQLite has no way to parameterise an identifier.
	/// </summary>
	public static String NameFor(String modelVersionId)
	{
		StringBuilder builder = new(Prefix);

		foreach (Char character in modelVersionId)
		{
			builder.Append(Char.IsLetterOrDigit(character) ? character : '_');
		}

		return builder.ToString();
	}

	/// <summary>
	/// Every <c>vec0</c> virtual table currently in the database — one per model version written so far.
	///
	/// <para>
	/// The <c>CREATE VIRTUAL TABLE</c> filter is load-bearing. <c>vec0</c> backs each virtual table with a
	/// family of internal shadow tables, and every one of them shares the virtual table's name prefix.
	/// Matching on the name alone therefore hands back implementation detail alongside the real table —
	/// and the shadow tables have no <c>chunk_id</c> column to delete from.
	/// </para>
	/// </summary>
	public static IReadOnlyList<String> ExistingTables(
		SqliteConnection connection,
		SqliteTransaction? transaction = null)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = """
			SELECT name FROM sqlite_master
			WHERE type = 'table'
			  AND name LIKE 'vec_chunk_vector__%'
			  AND sql LIKE 'CREATE VIRTUAL TABLE%';
			""";

		List<String> names = [];

		using SqliteDataReader reader = command.ExecuteReader();
		while (reader.Read())
		{
			names.Add(reader.GetString(0));
		}

		return names;
	}
}

/// <summary>Writes vectors into a native <c>sqlite-vec</c> table — the candidate to the packed-blob baseline.</summary>
internal sealed class SqliteVecVectorStoreWriter : IVectorStoreWriter
{
	private const Int32 DeleteBatchSize = 500;

	public Boolean RequiresVectorExtension => true;

	public void EnsureSchema(
		SqliteConnection connection,
		SqliteTransaction transaction,
		String modelVersionId,
		Int32 vectorDimension)
	{
		String table = SqliteVecTable.NameFor(modelVersionId);

		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;

		// distance_metric=cosine, because the baseline ranks by cosine and the comparison only means
		// something if both rank the same way. vec0 defaults to L2, which for L2-normalised vectors
		// orders identically but does not produce a score that can be put beside the other backend's.
		command.CommandText =
			$"CREATE VIRTUAL TABLE IF NOT EXISTS {table} USING vec0("
			+ "chunk_id TEXT PRIMARY KEY, "
			+ $"embedding float[{vectorDimension}] distance_metric=cosine);";
		command.ExecuteNonQuery();
	}

	public void UpsertVector(
		SqliteConnection connection,
		SqliteTransaction transaction,
		String chunkId,
		String modelVersionId,
		IReadOnlyList<Single> vector,
		Int32 vectorDimension)
	{
		String table = SqliteVecTable.NameFor(modelVersionId);

		// vec0 has no ON CONFLICT, so an upsert is delete-then-insert. Landing on an existing chunk id
		// only happens after a model switch anyway: chunk ids are content-addressed, so an edit mints
		// a new one rather than overwriting the old.
		using (SqliteCommand delete = connection.CreateCommand())
		{
			delete.Transaction = transaction;
			delete.CommandText = $"DELETE FROM {table} WHERE chunk_id = $chunkId;";
			delete.Parameters.AddWithValue("$chunkId", chunkId);
			delete.ExecuteNonQuery();
		}

		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = $"INSERT INTO {table} (chunk_id, embedding) VALUES ($chunkId, $embedding);";
		command.Parameters.AddWithValue("$chunkId", chunkId);
		command.Parameters.AddWithValue("$embedding", VectorBlob.Pack(vector));
		command.ExecuteNonQuery();
	}

	/// <summary>
	/// Deletes across every model version's table, matching the baseline's semantics: a chunk that no
	/// longer exists retires its vector in every embedding space, not only the active one.
	/// </summary>
	public void DeleteVectors(
		SqliteConnection connection,
		SqliteTransaction transaction,
		IReadOnlyList<String> chunkIds)
	{
		foreach (String table in SqliteVecTable.ExistingTables(connection, transaction))
		{
			for (Int32 offset = 0; offset < chunkIds.Count; offset += DeleteBatchSize)
			{
				Int32 batchSize = Math.Min(DeleteBatchSize, chunkIds.Count - offset);

				using SqliteCommand command = connection.CreateCommand();
				command.Transaction = transaction;

				String[] parameterNames = new String[batchSize];
				for (Int32 i = 0; i < batchSize; i++)
				{
					parameterNames[i] = $"$id{i}";
					command.Parameters.AddWithValue(parameterNames[i], chunkIds[offset + i]);
				}

				command.CommandText =
					$"DELETE FROM {table} WHERE chunk_id IN ({String.Join(", ", parameterNames)});";
				command.ExecuteNonQuery();
			}
		}
	}
}

/// <summary>
/// Reads vectors back out of the <c>vec0</c> tables.
///
/// <para>
/// The manifest-facing reads — chunk locations and the fit artifact — are storage-agnostic: they hit
/// <c>chunk_manifest</c>, <c>file_manifest</c> and <c>embedding_fit_artifact</c>, which both backends
/// share. They are delegated rather than duplicated, so there is one implementation of them to be wrong.
/// </para>
/// </summary>
internal sealed class SqliteVecVectorStoreReader : IVectorStoreReader
{
	private readonly SqliteBlobVectorStoreReader _manifestReads = new();

	public IReadOnlyList<StoredVector> ReadVectorsByModelVersion(String databasePath, String modelVersionId)
	{
		String table = SqliteVecTable.NameFor(modelVersionId);

		using SqliteConnection connection =
			FolderDatabaseConnection.OpenRead(databasePath, withVectorExtension: true);

		if (!SqliteVecTable.ExistingTables(connection).Contains(table))
		{
			return [];
		}

		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = $"SELECT chunk_id, embedding FROM {table};";

		List<StoredVector> results = [];

		using SqliteDataReader reader = command.ExecuteReader();
		while (reader.Read())
		{
			using Stream blob = reader.GetStream(1);
			Byte[] buffer = new Byte[(Int32)blob.Length];
			blob.ReadExactly(buffer);

			results.Add(new StoredVector(reader.GetString(0), VectorBlob.Unpack(buffer)));
		}

		return results;
	}

	public IReadOnlySet<String> ReadFileIdsWithVectors(String databasePath, String modelVersionId)
	{
		String table = SqliteVecTable.NameFor(modelVersionId);

		using SqliteConnection connection =
			FolderDatabaseConnection.OpenRead(databasePath, withVectorExtension: true);

		HashSet<String> fileIds = new(StringComparer.OrdinalIgnoreCase);

		if (!SqliteVecTable.ExistingTables(connection).Contains(table))
		{
			return fileIds;
		}

		using SqliteCommand command = connection.CreateCommand();
		command.CommandText =
			$"SELECT DISTINCT cm.file_id FROM {table} v JOIN chunk_manifest cm ON cm.chunk_id = v.chunk_id;";

		using SqliteDataReader reader = command.ExecuteReader();
		while (reader.Read())
		{
			fileIds.Add(reader.GetString(0));
		}

		return fileIds;
	}

	public IReadOnlyDictionary<String, ChunkLocation> ReadChunkLocations(
		String databasePath,
		IReadOnlyList<String> chunkIds)
		=> this._manifestReads.ReadChunkLocations(databasePath, chunkIds);

	public String? ReadFitArtifact(String databasePath, String modelVersionId)
		=> this._manifestReads.ReadFitArtifact(databasePath, modelVersionId);
}
