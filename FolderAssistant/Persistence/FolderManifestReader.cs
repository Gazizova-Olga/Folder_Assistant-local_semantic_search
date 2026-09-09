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
		command.CommandText = """
			SELECT
				fm.file_id,
				fm.file_hash,
				EXISTS (
					SELECT 1
					FROM chunk_manifest cm
					JOIN chunk_vector cv ON cv.chunk_id = cm.chunk_id AND cv.model_version_id = $modelVersionId
					WHERE cm.file_id = fm.file_id
				) AS has_vectors
			FROM file_manifest fm;
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
