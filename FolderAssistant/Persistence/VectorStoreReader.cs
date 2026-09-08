using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FolderAssistant.Persistence;

/// <summary>A stored vector, joined to the manifest rows that say where its text came from.</summary>
internal sealed record StoredChunkVector(
	String ChunkId,
	String FilePath,
	Int32 ChunkIndex,
	Int32 TokenStart,
	Int32 TokenEnd,
	IReadOnlyList<Single> Vector);

/// <summary>
/// The read half of the vector store, and the counterpart to <see cref="IVectorStoreWriter"/>.
///
/// <para>
/// Reads go behind a contract for the same reason writes do: it is what lets the stored
/// representation change without a retrieval strategy knowing. Today a vector is JSON text in a
/// column; a native vector extension would hold it as packed binary in a table of its own, and
/// nothing above this interface should have to care which.
/// </para>
/// </summary>
internal interface IVectorStoreReader
{
	IReadOnlyList<StoredChunkVector> ReadByModelVersion(String databasePath, String modelVersionId);

	/// <summary>
	/// The persisted fit for a model version, or <c>null</c> where the implementation has none.
	/// A vectorizer that does not depend on corpus statistics has nothing to restore.
	/// </summary>
	String? ReadFitArtifact(String databasePath, String modelVersionId);
}

/// <summary>Reads vectors stored as JSON arrays in <c>chunk_vector.vector_json</c>.</summary>
internal sealed class SqliteJsonVectorStoreReader : IVectorStoreReader
{
	public IReadOnlyList<StoredChunkVector> ReadByModelVersion(String databasePath, String modelVersionId)
	{
		using SqliteConnection connection = OpenRead(databasePath);
		using SqliteCommand command = connection.CreateCommand();

		// The join is what makes a hit locatable: a vector on its own says how well something matched
		// but not what matched, and chunks deliberately store no text of their own.
		command.CommandText = """
			SELECT cv.chunk_id, fm.file_path, cm.chunk_index, cm.token_start, cm.token_end, cv.vector_json
			FROM chunk_vector cv
			JOIN chunk_manifest cm ON cm.chunk_id = cv.chunk_id
			JOIN file_manifest fm ON fm.file_id = cm.file_id
			WHERE cv.model_version_id = $modelVersionId;
			""";
		command.Parameters.AddWithValue("$modelVersionId", modelVersionId);

		List<StoredChunkVector> results = [];

		using SqliteDataReader reader = command.ExecuteReader();
		while (reader.Read())
		{
			results.Add(new StoredChunkVector(
				ChunkId: reader.GetString(0),
				FilePath: reader.GetString(1),
				ChunkIndex: reader.GetInt32(2),
				TokenStart: reader.GetInt32(3),
				TokenEnd: reader.GetInt32(4),
				Vector: JsonSerializer.Deserialize<Single[]>(reader.GetString(5)) ?? []));
		}

		return results;
	}

	public String? ReadFitArtifact(String databasePath, String modelVersionId)
	{
		using SqliteConnection connection = OpenRead(databasePath);
		using SqliteCommand command = connection.CreateCommand();

		command.CommandText = "SELECT artifact_json FROM embedding_fit_artifact WHERE model_version_id = $id;";
		command.Parameters.AddWithValue("$id", modelVersionId);

		return command.ExecuteScalar() as String;
	}

	private static SqliteConnection OpenRead(String databasePath)
	{
		SqliteConnection connection = new(new SqliteConnectionStringBuilder
		{
			DataSource = databasePath,
			Mode = SqliteOpenMode.ReadOnly,
			Cache = SqliteCacheMode.Shared,
		}.ToString());

		connection.Open();

		return connection;
	}
}
