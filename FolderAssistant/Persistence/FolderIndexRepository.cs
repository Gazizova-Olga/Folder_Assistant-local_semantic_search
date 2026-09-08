using FolderAssistant.Embedding;
using FolderAssistant.Indexing;
using Microsoft.Data.Sqlite;

namespace FolderAssistant.Persistence;

/// <summary>What a single indexing pass wrote.</summary>
internal sealed record IndexWriteSummary(Int32 FilesUpserted, Int32 ChunksUpserted, Int32 VectorsUpserted);

/// <summary>
/// Writes a whole indexing pass to the database, in one transaction.
///
/// <para>
/// One transaction because the three tables are only meaningful together: a chunk row whose vector
/// did not land is a chunk that can never be retrieved, and it looks exactly like a chunk nothing
/// matches.
/// </para>
/// </summary>
internal sealed class FolderIndexRepository
{
	private readonly IVectorStoreWriter _vectorStoreWriter;

	public FolderIndexRepository()
		: this(new SqliteJsonVectorStoreWriter())
	{
	}

	internal FolderIndexRepository(IVectorStoreWriter vectorStoreWriter)
	{
		ArgumentNullException.ThrowIfNull(vectorStoreWriter);

		this._vectorStoreWriter = vectorStoreWriter;
	}

	public IndexWriteSummary Upsert(
		String databasePath,
		IReadOnlyList<ScannedTextFile> files,
		IReadOnlyDictionary<String, IReadOnlyList<TextChunk>> chunksByFile,
		IReadOnlyDictionary<String, EmbeddingResult> embeddingsByChunk,
		ModelDescriptor descriptor,
		String? fitArtifactJson = null)
	{
		ArgumentNullException.ThrowIfNull(files);
		ArgumentNullException.ThrowIfNull(chunksByFile);
		ArgumentNullException.ThrowIfNull(embeddingsByChunk);
		ArgumentNullException.ThrowIfNull(descriptor);

		String modelVersionId = descriptor.ModelVersionId;

		using SqliteConnection connection = new(new SqliteConnectionStringBuilder
		{
			DataSource = databasePath,
			Mode = SqliteOpenMode.ReadWrite,
			Cache = SqliteCacheMode.Shared,
		}.ToString());

		connection.Open();

		using SqliteTransaction transaction = connection.BeginTransaction();

		UpsertModel(connection, transaction, descriptor);

		// Written in the same transaction as the vectors it produced. An artifact that disagrees with
		// the stored vectors corrupts every query embedded against it, and nothing would report that.
		if (fitArtifactJson is not null)
		{
			UpsertFitArtifact(connection, transaction, modelVersionId, fitArtifactJson);
		}

		Int32 fileCount = 0;
		Int32 chunkCount = 0;
		Int32 vectorCount = 0;

		foreach (ScannedTextFile file in files)
		{
			UpsertFile(connection, transaction, file);
			fileCount++;

			if (!chunksByFile.TryGetValue(file.FileId, out IReadOnlyList<TextChunk>? chunks))
			{
				continue;
			}

			foreach (TextChunk chunk in chunks)
			{
				UpsertChunk(connection, transaction, file.FileId, chunk, modelVersionId);
				chunkCount++;

				if (!embeddingsByChunk.TryGetValue(chunk.ChunkId, out EmbeddingResult? embedding))
				{
					continue;
				}

				this._vectorStoreWriter.UpsertVector(
					connection, transaction, chunk.ChunkId, modelVersionId, embedding.Vector, descriptor.Dimension);
				vectorCount++;
			}
		}

		transaction.Commit();

		return new IndexWriteSummary(fileCount, chunkCount, vectorCount);
	}

	private static void UpsertModel(
		SqliteConnection connection,
		SqliteTransaction transaction,
		ModelDescriptor descriptor)
	{
		// Exactly one model version may be active for write. Activating this one demotes the rest in the
		// same transaction — the insert below hardcoded is_active_for_write to 1, so indexing a folder
		// with a second model left both rows claiming it.
		using (SqliteCommand demote = connection.CreateCommand())
		{
			demote.Transaction = transaction;
			demote.CommandText =
				"UPDATE embedding_model_registry SET is_active_for_write = 0 WHERE model_version_id <> $id;";
			demote.Parameters.AddWithValue("$id", descriptor.ModelVersionId);
			demote.ExecuteNonQuery();
		}

		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = """
			INSERT INTO embedding_model_registry (
				model_version_id, provider_type, model_name, vector_dimension,
				distance_metric, is_active_for_write, activated_utc)
			VALUES ($id, $providerType, $modelName, $dimension, $metric, 1, $activated)
			ON CONFLICT(model_version_id) DO UPDATE SET
				provider_type = excluded.provider_type,
				model_name = excluded.model_name,
				vector_dimension = excluded.vector_dimension,
				distance_metric = excluded.distance_metric,
				is_active_for_write = excluded.is_active_for_write,
				activated_utc = excluded.activated_utc;
			""";
		command.Parameters.AddWithValue("$id", descriptor.ModelVersionId);
		command.Parameters.AddWithValue("$providerType", descriptor.ProviderType);
		command.Parameters.AddWithValue("$modelName", descriptor.ModelName);
		command.Parameters.AddWithValue("$dimension", descriptor.Dimension);
		command.Parameters.AddWithValue("$metric", descriptor.DistanceMetric);
		command.Parameters.AddWithValue("$activated", UtcNow());
		command.ExecuteNonQuery();
	}

	private static void UpsertFitArtifact(
		SqliteConnection connection,
		SqliteTransaction transaction,
		String modelVersionId,
		String artifactJson)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = """
			INSERT INTO embedding_fit_artifact (model_version_id, artifact_json, created_utc)
			VALUES ($id, $artifact, $created)
			ON CONFLICT(model_version_id) DO UPDATE SET
				artifact_json = excluded.artifact_json,
				created_utc = excluded.created_utc;
			""";
		command.Parameters.AddWithValue("$id", modelVersionId);
		command.Parameters.AddWithValue("$artifact", artifactJson);
		command.Parameters.AddWithValue("$created", UtcNow());
		command.ExecuteNonQuery();
	}

	private static void UpsertFile(SqliteConnection connection, SqliteTransaction transaction, ScannedTextFile file)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = """
			INSERT INTO file_manifest (
				file_id, file_path, file_hash, size_bytes, modified_utc, status, updated_utc)
			VALUES ($id, $path, $hash, $size, $modified, 'active', $updated)
			ON CONFLICT(file_id) DO UPDATE SET
				file_path = excluded.file_path,
				file_hash = excluded.file_hash,
				size_bytes = excluded.size_bytes,
				modified_utc = excluded.modified_utc,
				status = excluded.status,
				updated_utc = excluded.updated_utc;
			""";
		command.Parameters.AddWithValue("$id", file.FileId);
		command.Parameters.AddWithValue("$path", file.RelativePath);
		command.Parameters.AddWithValue("$hash", file.FileHash);
		command.Parameters.AddWithValue("$size", file.SizeBytes);
		command.Parameters.AddWithValue("$modified", file.ModifiedUtc.ToString("O"));
		command.Parameters.AddWithValue("$updated", UtcNow());
		command.ExecuteNonQuery();
	}

	private static void UpsertChunk(
		SqliteConnection connection,
		SqliteTransaction transaction,
		String fileId,
		TextChunk chunk,
		String modelVersionId)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = """
			INSERT INTO chunk_manifest (
				chunk_id, file_id, chunk_index, token_start, token_end,
				chunk_hash, model_version_id, updated_utc)
			VALUES ($chunkId, $fileId, $index, $tokenStart, $tokenEnd, $hash, $modelVersion, $updated)
			ON CONFLICT(chunk_id) DO UPDATE SET
				file_id = excluded.file_id,
				chunk_index = excluded.chunk_index,
				token_start = excluded.token_start,
				token_end = excluded.token_end,
				chunk_hash = excluded.chunk_hash,
				model_version_id = excluded.model_version_id,
				updated_utc = excluded.updated_utc;
			""";
		command.Parameters.AddWithValue("$chunkId", chunk.ChunkId);
		command.Parameters.AddWithValue("$fileId", fileId);
		command.Parameters.AddWithValue("$index", chunk.Index);
		command.Parameters.AddWithValue("$tokenStart", chunk.TokenStart);
		command.Parameters.AddWithValue("$tokenEnd", chunk.TokenEnd);
		command.Parameters.AddWithValue("$hash", chunk.ChunkHash);
		command.Parameters.AddWithValue("$modelVersion", modelVersionId);
		command.Parameters.AddWithValue("$updated", UtcNow());
		command.ExecuteNonQuery();
	}

	private static String UtcNow() => DateTime.UtcNow.ToString("O");
}
