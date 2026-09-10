using Microsoft.Data.Sqlite;

namespace FolderAssistant.Persistence;

/// <summary>A stored vector, carrying only what scoring needs.</summary>
internal sealed record StoredVector(String ChunkId, IReadOnlyList<Single> Vector);

/// <summary>
/// Where a chunk's text came from. Resolved for ranked hits only, never for every candidate.
/// </summary>
internal sealed record ChunkLocation(
	String ChunkId,
	String FilePath,
	Int32 ChunkIndex,
	Int32 TokenStart,
	Int32 TokenEnd);

/// <summary>
/// The read half of the vector store, and the counterpart to <see cref="IVectorStoreWriter"/>.
///
/// <para>
/// Reads go behind a contract for the same reason writes do: it is what let the stored
/// representation change from JSON text to packed <c>float32</c> blobs without a retrieval
/// strategy knowing, and what would let a native vector extension's virtual table replace it in
/// turn.
/// </para>
///
/// <para>
/// **Reading is split in two on purpose.** Scoring needs a chunk id and a vector and nothing else.
/// The file path and token offsets matter only for the handful of chunks that actually rank, so
/// fetching them for every candidate means joining <c>chunk_manifest</c> and <c>file_manifest</c>
/// across the whole corpus to return five rows — metadata for candidates that are about to be
/// discarded. Measurements are in <c>SPEC-131</c>.
/// </para>
/// </summary>
internal interface IVectorStoreReader
{
	/// <summary>Every vector of a model version, for scoring. No manifest joins.</summary>
	IReadOnlyList<StoredVector> ReadVectorsByModelVersion(String databasePath, String modelVersionId);

	/// <summary>
	/// The files that already have at least one vector under this model version.
	///
	/// <para>
	/// This has to come from the store rather than from a query the manifest reader writes for itself,
	/// because only the store knows where its vectors live. A backend that keeps them in a virtual
	/// table leaves <c>chunk_vector</c> empty, so a hardcoded lookup there would report that no file
	/// has ever been embedded — and every file would then be re-embedded on every run, silently, with
	/// the index still looking entirely correct.
	/// </para>
	/// </summary>
	IReadOnlySet<String> ReadFileIdsWithVectors(String databasePath, String modelVersionId);

	/// <summary>Resolves source locations for chunks that have already been ranked.</summary>
	IReadOnlyDictionary<String, ChunkLocation> ReadChunkLocations(
		String databasePath,
		IReadOnlyList<String> chunkIds);

	/// <summary>
	/// The persisted fit for a model version, or <c>null</c> where the implementation has none.
	/// A vectorizer that does not depend on corpus statistics has nothing to restore.
	/// </summary>
	String? ReadFitArtifact(String databasePath, String modelVersionId);
}

/// <summary>Reads vectors stored as packed <c>float32</c> blobs in <c>chunk_vector.vector</c>.</summary>
internal sealed class SqliteBlobVectorStoreReader : IVectorStoreReader
{
	public IReadOnlySet<String> ReadFileIdsWithVectors(String databasePath, String modelVersionId)
	{
		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(databasePath);

		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = """"""
			SELECT DISTINCT cm.file_id
			FROM chunk_vector cv
			JOIN chunk_manifest cm ON cm.chunk_id = cv.chunk_id
			WHERE cv.model_version_id = $modelVersionId;
			"""""";
		command.Parameters.AddWithValue("$modelVersionId", modelVersionId);

		HashSet<String> fileIds = new(StringComparer.OrdinalIgnoreCase);

		using SqliteDataReader reader = command.ExecuteReader();
		while (reader.Read())
		{
			fileIds.Add(reader.GetString(0));
		}

		return fileIds;
	}

	public IReadOnlyList<StoredVector> ReadVectorsByModelVersion(String databasePath, String modelVersionId)
	{
		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(databasePath);
		using SqliteCommand command = connection.CreateCommand();

		// No joins. This runs over every vector of the model version, so anything added here is
		// paid for by candidates that will not be returned.
		command.CommandText = """
			SELECT chunk_id, vector
			FROM chunk_vector
			WHERE model_version_id = $modelVersionId;
			""";
		command.Parameters.AddWithValue("$modelVersionId", modelVersionId);

		List<StoredVector> results = [];

		using SqliteDataReader reader = command.ExecuteReader();

		while (reader.Read())
		{
			using Stream blob = reader.GetStream(1);
			using MemoryStream buffer = new();
			blob.CopyTo(buffer);

			results.Add(new StoredVector(reader.GetString(0), VectorBlob.Unpack(buffer.GetBuffer().AsSpan(0, (Int32)buffer.Length))));
		}

		return results;
	}

	// Manifest-facing reads are the same whatever holds the vectors, so they live in a type both
	// stores use rather than in one of them: SPEC-150 forbids an implementation depending on a
	// concrete implementation, and there is no assembly boundary here to enforce that mechanically.
	public IReadOnlyDictionary<String, ChunkLocation> ReadChunkLocations(
		String databasePath,
		IReadOnlyList<String> chunkIds)
		=> ManifestReads.ReadChunkLocations(databasePath, chunkIds);

	public String? ReadFitArtifact(String databasePath, String modelVersionId)
		=> ManifestReads.ReadFitArtifact(databasePath, modelVersionId);
}
