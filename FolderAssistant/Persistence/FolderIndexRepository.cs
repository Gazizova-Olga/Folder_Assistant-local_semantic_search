using FolderAssistant.Embedding;
using FolderAssistant.Indexing;
using Microsoft.Data.Sqlite;

namespace FolderAssistant.Persistence;

/// <summary>What a single write of chunks and vectors covered.</summary>
internal sealed record IndexWriteSummary(
	Int32 FilesWritten,
	Int32 ChunksUpserted,
	Int32 VectorsUpserted);

/// <summary>
/// Writes the embedding side of the index — chunks, vectors, the model registry and the fit artifact —
/// in one transaction per call.
///
/// <para>
/// One transaction because those tables are only meaningful together: a chunk row whose vector did
/// not land is a chunk that can never be retrieved, and it looks exactly like a chunk nothing matches.
/// </para>
///
/// <para>
/// <strong>It never writes a file's record.</strong> What a <c>file_manifest</c> row says — the path,
/// the content hash, the size, the creation time, whether the file is still there, and what was last
/// delivered for it — is written by the indexing store and by nothing else (<c>SPEC-121</c>, own your
/// columns). This type used to write those rows too, and a delivery that had read a file's hash before
/// a slow embed wrote it back afterwards, reverting a newer hash the front end had recorded in between:
/// the delivery queued for the newer content then found its work already done and skipped, and the file
/// sat on stale vectors until a periodic pass happened to re-hash it. A chunk row references the file
/// row the store wrote, so writing chunks for a file the store has not recorded fails on the foreign
/// key — which is the right failure, rather than a row this side invents.
/// </para>
///
/// <para>
/// The one thing it does to a file row is end it: <see cref="DeleteFile"/> removes the row, and only
/// as the last step of removing the file's vectors and chunks on a delivered removal, because vectors
/// must go before the cascade and only this side can reach them. Even that is conditional — a row the
/// store has made active again is a file that came back while its removal waited in the queue, and
/// ending it would take the chunks of the file the folder now holds.
/// </para>
/// </summary>
internal sealed class FolderIndexRepository
{
	private readonly IVectorStoreWriter _vectorStoreWriter;

	public FolderIndexRepository()
		: this(new SqliteBlobVectorStoreWriter())
	{
	}

	internal FolderIndexRepository(IVectorStoreWriter vectorStoreWriter)
	{
		ArgumentNullException.ThrowIfNull(vectorStoreWriter);

		this._vectorStoreWriter = vectorStoreWriter;
	}

	/// <summary>
	/// Writes the chunks and vectors of many files at once, with the model registry entry and, where
	/// there is one, the fit they were produced under. Takes metadata, not text.
	///
	/// <para>
	/// <c>chunk_manifest</c> holds chunk ids, offsets and hashes and no content at all, so a
	/// signature demanding text the write does not store would oblige the pipeline to keep the whole
	/// corpus in memory purely to satisfy it. The narrower types are the guard: there is no field
	/// here to put a corpus in.
	/// </para>
	///
	/// <para>
	/// Every file named in <paramref name="chunksByFile"/> must already have its record in the store.
	/// A file whose chunk list is empty has its existing chunks cleared and nothing written.
	/// </para>
	/// </summary>
	public IndexWriteSummary Upsert(
		String databasePath,
		IReadOnlyDictionary<String, IReadOnlyList<ChunkMetadata>> chunksByFile,
		IReadOnlyDictionary<String, EmbeddingResult> embeddingsByChunk,
		ModelDescriptor descriptor,
		String? fitArtifactJson = null)
	{
		ArgumentNullException.ThrowIfNull(chunksByFile);
		ArgumentNullException.ThrowIfNull(embeddingsByChunk);
		ArgumentNullException.ThrowIfNull(descriptor);

		String modelVersionId = descriptor.ModelVersionId;

		// foreign_keys is per connection in SQLite, not per database, so what the bootstrap set does
		// not carry here — and the deletions below rely on the cascade. The connection factory sets
		// it, along with the busy timeout this call site never had.
		using SqliteConnection connection = FolderDatabaseConnection.OpenWrite(
			databasePath,
			withVectorExtension: this._vectorStoreWriter.RequiresVectorExtension);

		using SqliteTransaction transaction = connection.BeginTransaction();

		UpsertModel(connection, transaction, descriptor);

		// The vector store creates its own tables. A native backend's are virtual tables whose shape —
		// down to a vector dimension fixed at creation — only the backend knows, so the bootstrapper
		// cannot create them on its behalf.
		this._vectorStoreWriter.EnsureSchema(connection, transaction, modelVersionId, descriptor.Dimension);

		// Written in the same transaction as the vectors it produced. An artifact that disagrees with
		// the stored vectors corrupts every query embedded against it, and nothing would report that.
		if (fitArtifactJson is not null)
		{
			UpsertFitArtifact(connection, transaction, modelVersionId, fitArtifactJson);
		}

		Int32 fileCount = 0;
		Int32 chunkCount = 0;
		Int32 vectorCount = 0;

		foreach ((String fileId, IReadOnlyList<ChunkMetadata> chunks) in chunksByFile)
		{
			fileCount++;

			(Int32 chunksWritten, Int32 vectorsWritten) = this.WriteChunks(
				connection, transaction, fileId, chunks, embeddingsByChunk, descriptor);

			chunkCount += chunksWritten;
			vectorCount += vectorsWritten;
		}

		transaction.Commit();

		return new IndexWriteSummary(fileCount, chunkCount, vectorCount);
	}

	/// <summary>
	/// Writes one delivered file's chunks and their vectors, in one transaction, under the id the
	/// delivery named. The file's record is the store's and is not touched here — see the type remarks
	/// for the race that writing it caused.
	///
	/// <para>
	/// It cannot write a fit artifact, and does not try. A corpus-fitted embedder is fitted against a
	/// whole corpus, and one delivered file is not one; the whole-folder pass owns that.
	/// </para>
	/// </summary>
	public IndexWriteSummary UpsertSingleFile(
		String databasePath,
		String fileId,
		IReadOnlyList<ChunkMetadata> chunks,
		IReadOnlyDictionary<String, EmbeddingResult> embeddingsByChunk,
		ModelDescriptor descriptor)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
		ArgumentNullException.ThrowIfNull(chunks);
		ArgumentNullException.ThrowIfNull(embeddingsByChunk);
		ArgumentNullException.ThrowIfNull(descriptor);

		String modelVersionId = descriptor.ModelVersionId;

		using SqliteConnection connection = FolderDatabaseConnection.OpenWrite(
			databasePath,
			withVectorExtension: this._vectorStoreWriter.RequiresVectorExtension);

		using SqliteTransaction transaction = connection.BeginTransaction();

		UpsertModel(connection, transaction, descriptor);
		this._vectorStoreWriter.EnsureSchema(connection, transaction, modelVersionId, descriptor.Dimension);

		(Int32 chunkCount, Int32 vectorCount) = this.WriteChunks(
			connection, transaction, fileId, chunks, embeddingsByChunk, descriptor);

		transaction.Commit();

		return new IndexWriteSummary(1, chunkCount, vectorCount);
	}

	/// <summary>
	/// Removes one file from the index: its vectors first, then the row whose cascade takes its chunks.
	///
	/// <para>
	/// The order is the load-bearing part. Vectors go through the store's own contract rather than the
	/// cascade, because the cascade is a property of the blob backend and a native store keeps vectors
	/// in a virtual table that cannot be a foreign-key target. Deleting the file row first would leave
	/// such a store holding vectors for chunks that no longer exist, with nothing left to name them by.
	/// </para>
	///
	/// <para>
	/// <strong>The row is ended only while it is still marked removed</strong>, and the condition is
	/// read inside this transaction rather than trusted from the caller. The dispatcher already asks
	/// whether the file came back before it delivers a removal, but a return landing after that
	/// question and before this write would have had its row ended anyway — and then the upsert queued
	/// behind the removal found nothing recorded and skipped, leaving a file on disk absent from every
	/// search until a periodic pass rediscovered it. That window is one store round-trip wide, which
	/// is narrow and not closed by being narrow. Here it closes: the transaction takes the write lock
	/// at <c>BEGIN</c>, so the status this reads is the status the delete acts on, and a revival either
	/// precedes it or waits for it (<c>SPEC-121</c>, <c>SPEC-130</c>).
	/// </para>
	///
	/// <para>
	/// A file that was never indexed is not an error, and neither is one that came back. Delivery is
	/// at-least-once, so a delete can arrive twice, or arrive for a file whose upsert was skipped.
	/// Both report <see langword="false"/>: nothing was ended.
	/// </para>
	/// </summary>
	public Boolean DeleteFile(String databasePath, String fileId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

		using SqliteConnection connection = FolderDatabaseConnection.OpenWrite(
			databasePath,
			withVectorExtension: this._vectorStoreWriter.RequiresVectorExtension);

		using SqliteTransaction transaction = connection.BeginTransaction();

		// Before the vectors, not only before the row: a file that is recorded again keeps what was
		// embedded for it, and the upsert queued behind this removal supersedes those chunks itself.
		if (!IsStillRemoved(connection, transaction, fileId))
		{
			return false;
		}

		this.DeleteVectorsOf(connection, transaction,
			"SELECT chunk_id FROM chunk_manifest WHERE file_id = $fileId;",
			command => command.Parameters.AddWithValue("$fileId", fileId));

		Int32 deleted;

		using (SqliteCommand command = connection.CreateCommand())
		{
			command.Transaction = transaction;
			command.CommandText = "DELETE FROM file_manifest WHERE file_id = $fileId;";
			command.Parameters.AddWithValue("$fileId", fileId);
			deleted = command.ExecuteNonQuery();
		}

		transaction.Commit();

		return deleted > 0;
	}

	/// <summary>
	/// Replaces one file's chunks: whatever the current chunk set no longer contains goes first, then
	/// each current chunk and its vector.
	///
	/// <para>
	/// The deletion comes first for a reason the constraint enforces: an edit produces a new
	/// content-addressed id for the same <c>(file_id, chunk_index)</c> slot, and the unique constraint
	/// on that pair would reject it while the old row is still there.
	/// </para>
	/// </summary>
	private (Int32 Chunks, Int32 Vectors) WriteChunks(
		SqliteConnection connection,
		SqliteTransaction transaction,
		String fileId,
		IReadOnlyList<ChunkMetadata> chunks,
		IReadOnlyDictionary<String, EmbeddingResult> embeddingsByChunk,
		ModelDescriptor descriptor)
	{
		this.DeleteSupersededChunks(connection, transaction, fileId, chunks);

		Int32 chunkCount = 0;
		Int32 vectorCount = 0;

		foreach (ChunkMetadata chunk in chunks)
		{
			UpsertChunk(connection, transaction, fileId, chunk, descriptor.ModelVersionId);
			chunkCount++;

			if (!embeddingsByChunk.TryGetValue(chunk.ChunkId, out EmbeddingResult? embedding))
			{
				continue;
			}

			this._vectorStoreWriter.UpsertVector(
				connection, transaction, chunk.ChunkId, descriptor.ModelVersionId, embedding.Vector, descriptor.Dimension);
			vectorCount++;
		}

		return (chunkCount, vectorCount);
	}

	/// <summary>
	/// Deletes the vectors of every chunk the given query selects, through the store's own contract
	/// rather than by relying on the <c>chunk_manifest</c> cascade.
	///
	/// <para>
	/// The cascade does fire, so this looks redundant. It is not: the cascade is a property of the
	/// SQLite blob backend, not of <see cref="IVectorStoreWriter"/>. A native vector extension keeps
	/// vectors in a virtual table, and a virtual table cannot be the target of a foreign key — so a
	/// backend that could not honour the cascade is exactly the one this seam exists to allow.
	/// </para>
	/// </summary>
	private void DeleteVectorsOf(
		SqliteConnection connection,
		SqliteTransaction transaction,
		String selectChunkIds,
		Action<SqliteCommand> bind)
	{
		List<String> chunkIds = [];

		using (SqliteCommand select = connection.CreateCommand())
		{
			select.Transaction = transaction;
			select.CommandText = selectChunkIds;
			bind(select);

			using SqliteDataReader reader = select.ExecuteReader();

			while (reader.Read())
			{
				chunkIds.Add(reader.GetString(0));
			}
		}

		this._vectorStoreWriter.DeleteVectors(connection, transaction, chunkIds);
	}

	/// <summary>
	/// Whether the row this removal is about is still the removed one.
	///
	/// <para>
	/// The question is asked of the same column the manifest reader reads, and answered by the value
	/// the store writes when it marks a file gone. A row that is active again is a file that came back
	/// while its removal sat in the queue; no row at all is a file that was never indexed.
	/// </para>
	/// </summary>
	private static Boolean IsStillRemoved(
		SqliteConnection connection,
		SqliteTransaction transaction,
		String fileId)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "SELECT status FROM file_manifest WHERE file_id = $fileId;";
		command.Parameters.AddWithValue("$fileId", fileId);

		return command.ExecuteScalar() is String status
			&& String.Equals(status, FolderIndexStore.Deleted, StringComparison.Ordinal);
	}

	/// <summary>
	/// Removes chunk rows for a file that the current chunk set no longer produces.
	///
	/// <para>
	/// Two cases need it. A content edit yields a new content-addressed <c>chunk_id</c> for the same
	/// <c>(file_id, chunk_index)</c> slot, which the unique constraint on that pair would otherwise
	/// reject; and a file that shrank leaves trailing chunks behind with nothing to overwrite them.
	/// Their vectors cascade away, which is what keeps every stored vector bound to the content it
	/// was computed from, under every model version.
	/// </para>
	/// </summary>
	private void DeleteSupersededChunks(
		SqliteConnection connection,
		SqliteTransaction transaction,
		String fileId,
		IReadOnlyList<ChunkMetadata> currentChunks)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.Parameters.AddWithValue("$fileId", fileId);

		if (currentChunks.Count == 0)
		{
			this.DeleteVectorsOf(connection, transaction,
				"SELECT chunk_id FROM chunk_manifest WHERE file_id = $fileId;",
				c => c.Parameters.AddWithValue("$fileId", fileId));

			command.CommandText = "DELETE FROM chunk_manifest WHERE file_id = $fileId;";
			command.ExecuteNonQuery();

			return;
		}

		String[] keepParameters = new String[currentChunks.Count];

		for (Int32 i = 0; i < currentChunks.Count; i++)
		{
			keepParameters[i] = $"$keep{i}";
			command.Parameters.AddWithValue(keepParameters[i], currentChunks[i].ChunkId);
		}

		String doomed =
			$"SELECT chunk_id FROM chunk_manifest WHERE file_id = $fileId AND chunk_id NOT IN ({String.Join(", ", keepParameters)});";

		this.DeleteVectorsOf(connection, transaction, doomed, c =>
		{
			c.Parameters.AddWithValue("$fileId", fileId);
			for (Int32 i = 0; i < currentChunks.Count; i++)
			{
				c.Parameters.AddWithValue(keepParameters[i], currentChunks[i].ChunkId);
			}
		});

		command.CommandText =
			$"DELETE FROM chunk_manifest WHERE file_id = $fileId AND chunk_id NOT IN ({String.Join(", ", keepParameters)});";
		command.ExecuteNonQuery();
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

	private static void UpsertChunk(
		SqliteConnection connection,
		SqliteTransaction transaction,
		String fileId,
		ChunkMetadata chunk,
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
