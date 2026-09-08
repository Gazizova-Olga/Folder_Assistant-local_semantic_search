using System.Text.Json;
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
	public IndexWriteSummary Upsert(
		String databasePath,
		IReadOnlyList<ScannedTextFile> files,
		IReadOnlyDictionary<String, IReadOnlyList<TextChunk>> chunksByFile,
		IReadOnlyDictionary<String, EmbeddingResult> embeddingsByChunk,
		String modelVersionId,
		Int32 vectorDimension)
	{
		ArgumentNullException.ThrowIfNull(files);
		ArgumentNullException.ThrowIfNull(chunksByFile);
		ArgumentNullException.ThrowIfNull(embeddingsByChunk);

		using SqliteConnection connection = new(new SqliteConnectionStringBuilder
		{
			DataSource = databasePath,
			Mode = SqliteOpenMode.ReadWrite,
			Cache = SqliteCacheMode.Shared,
		}.ToString());

		connection.Open();

		using SqliteTransaction transaction = connection.BeginTransaction();

		UpsertModel(connection, transaction, modelVersionId, vectorDimension);

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

				UpsertVector(connection, transaction, chunk.ChunkId, modelVersionId, embedding.Vector, vectorDimension);
				vectorCount++;
			}
		}

		transaction.Commit();

		return new IndexWriteSummary(fileCount, chunkCount, vectorCount);
	}

	private static void UpsertModel(
		SqliteConnection connection,
		SqliteTransaction transaction,
		String modelVersionId,
		Int32 vectorDimension)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = """
			INSERT INTO embedding_model_registry (
				model_version_id, provider_type, model_name, vector_dimension,
				distance_metric, is_active_for_write, activated_utc)
			VALUES ($id, 'programmable', 'programmable-embedding', $dimension, 'cosine', 1, $activated)
			ON CONFLICT(model_version_id) DO UPDATE SET
				provider_type = excluded.provider_type,
				model_name = excluded.model_name,
				vector_dimension = excluded.vector_dimension,
				distance_metric = excluded.distance_metric,
				is_active_for_write = excluded.is_active_for_write,
				activated_utc = excluded.activated_utc;
			""";
		command.Parameters.AddWithValue("$id", modelVersionId);
		command.Parameters.AddWithValue("$dimension", vectorDimension);
		command.Parameters.AddWithValue("$activated", UtcNow());
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

	private static void UpsertVector(
		SqliteConnection connection,
		SqliteTransaction transaction,
		String chunkId,
		String modelVersionId,
		IReadOnlyList<Single> vector,
		Int32 vectorDimension)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = """
			INSERT INTO chunk_vector (
				chunk_id, model_version_id, vector_json, vector_dimension, updated_utc)
			VALUES ($chunkId, $modelVersionId, $vectorJson, $dimension, $updated)
			ON CONFLICT(chunk_id, model_version_id) DO UPDATE SET
				vector_json = excluded.vector_json,
				vector_dimension = excluded.vector_dimension,
				updated_utc = excluded.updated_utc;
			""";
		command.Parameters.AddWithValue("$chunkId", chunkId);
		command.Parameters.AddWithValue("$modelVersionId", modelVersionId);
		command.Parameters.AddWithValue("$vectorJson", JsonSerializer.Serialize(vector));
		command.Parameters.AddWithValue("$dimension", vectorDimension);
		command.Parameters.AddWithValue("$updated", UtcNow());
		command.ExecuteNonQuery();
	}

	private static String UtcNow() => DateTime.UtcNow.ToString("O");
}
