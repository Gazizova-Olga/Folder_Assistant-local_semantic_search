using Microsoft.Data.Sqlite;

namespace FolderAssistant.Persistence;

/// <summary>What the index already knows about one file, for one embedding model version.</summary>
internal sealed record IndexedFileState(String FileId, String FileHash, Boolean HasVectorsForModel);

/// <summary>
/// Reads back what has already been indexed, so a pass can tell what actually changed.
///
/// <para>
/// The file hash was being written from the first version of the manifest and never read. This is
/// what reads it.
/// </para>
/// </summary>
internal interface IFolderManifestReader
{
	IReadOnlyDictionary<String, IndexedFileState> ReadFileStates(String databasePath, String modelVersionId);
}

internal sealed class SqliteFolderManifestReader : IFolderManifestReader
{
	public IReadOnlyDictionary<String, IndexedFileState> ReadFileStates(String databasePath, String modelVersionId)
	{
		Dictionary<String, IndexedFileState> states = new(StringComparer.OrdinalIgnoreCase);

		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(databasePath);

		using SqliteCommand command = connection.CreateCommand();

		// The vector-existence check is per model version, not merely per file. A file whose content
		// has not changed still needs embedding when the active model has never seen it — which is
		// exactly the state after switching embedding implementation.
		//
		// The set of files that already have vectors is built ONCE and joined. Asking the question
		// per file instead — with a correlated EXISTS — plans catastrophically: with no table
		// statistics SQLite drives the inner query off the model-version index, so every outer file
		// row walks every vector of that model before filtering by file id. That is O(files x
		// vectors), and measured on this code it took 97 seconds for 4,000 files against 24,000
		// vectors. Small folders hide it completely, because the plan only turns pathological once
		// chunk_vector is large.
		command.CommandText = """
			SELECT
				fm.file_id,
				fm.file_hash,
				CASE WHEN v.file_id IS NULL THEN 0 ELSE 1 END AS has_vectors
			FROM file_manifest fm
			LEFT JOIN (
				SELECT DISTINCT cm.file_id AS file_id
				FROM chunk_vector cv
				JOIN chunk_manifest cm ON cm.chunk_id = cv.chunk_id
				WHERE cv.model_version_id = $modelVersionId
			) v ON v.file_id = fm.file_id;
			""";
		command.Parameters.AddWithValue("$modelVersionId", modelVersionId);

		using SqliteDataReader reader = command.ExecuteReader();

		while (reader.Read())
		{
			String fileId = reader.GetString(0);

			states[fileId] = new IndexedFileState(
				FileId: fileId,
				FileHash: reader.GetString(1),
				HasVectorsForModel: reader.GetInt32(2) != 0);
		}

		return states;
	}
}
