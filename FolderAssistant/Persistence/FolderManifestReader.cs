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
	private readonly IVectorStoreReader _vectorStoreReader;

	public SqliteFolderManifestReader()
		: this(new SqliteBlobVectorStoreReader())
	{
	}

	internal SqliteFolderManifestReader(IVectorStoreReader vectorStoreReader)
	{
		ArgumentNullException.ThrowIfNull(vectorStoreReader);

		this._vectorStoreReader = vectorStoreReader;
	}

	public IReadOnlyDictionary<String, IndexedFileState> ReadFileStates(String databasePath, String modelVersionId)
	{
		// The vector-existence check is per model version, not merely per file. A file whose content
		// has not changed still needs embedding when the active model has never seen it — which is
		// exactly the state after switching embedding implementation.
		//
		// It is asked of the vector store rather than answered here, because only the store knows
		// where its vectors live. A backend keeping them in a virtual table leaves chunk_vector empty,
		// so a query written here against that table would report that nothing has ever been embedded
		// — and every file would be re-embedded on every run, silently, with the index still looking
		// entirely correct.
		//
		// The set is fetched once and joined in memory. Asking the question per file instead — with a
		// correlated EXISTS — plans catastrophically: with no table statistics SQLite drives the inner
		// query off the model-version index, so every outer file row walks every vector of that model
		// before filtering by file id. That is O(files x vectors), and measured on this code it took
		// 97 seconds for 4,000 files against 24,000 vectors. Small folders hide it completely, because
		// the plan only turns pathological once the vector table is large.
		IReadOnlySet<String> filesWithVectors =
			this._vectorStoreReader.ReadFileIdsWithVectors(databasePath, modelVersionId);

		Dictionary<String, IndexedFileState> states = new(StringComparer.OrdinalIgnoreCase);

		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(databasePath);

		// Active rows only. A row whose removal is recorded and not yet delivered still has its chunks
		// and vectors, and is on its way out: a pass must not embed against it. A file that reappears
		// under that path is the next comparison's to record, and the pass embeds it then.
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = $"SELECT file_id, file_hash FROM file_manifest WHERE status = '{FolderIndexStore.Active}';";

		using SqliteDataReader reader = command.ExecuteReader();

		while (reader.Read())
		{
			String fileId = reader.GetString(0);

			states[fileId] = new IndexedFileState(
				FileId: fileId,
				FileHash: reader.GetString(1),
				HasVectorsForModel: filesWithVectors.Contains(fileId));
		}

		return states;
	}
}
