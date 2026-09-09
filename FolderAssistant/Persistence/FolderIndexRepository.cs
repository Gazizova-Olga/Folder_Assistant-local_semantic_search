using FolderAssistant.Embedding;
using FolderAssistant.Indexing;
using Microsoft.Data.Sqlite;

namespace FolderAssistant.Persistence;

/// <summary>What a single indexing pass wrote.</summary>
internal sealed record IndexWriteSummary(
	Int32 FilesUpserted,
	Int32 ChunksUpserted,
	Int32 VectorsUpserted,
	Int32 FilesDeleted);

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
		: this(new SqliteBlobVectorStoreWriter())
	{
	}

	internal FolderIndexRepository(IVectorStoreWriter vectorStoreWriter)
	{
		ArgumentNullException.ThrowIfNull(vectorStoreWriter);

		this._vectorStoreWriter = vectorStoreWriter;
	}

	/// <summary>
	/// Writes the index. Takes metadata, not text.
	///
	/// <para>
	/// <c>chunk_manifest</c> holds chunk ids, offsets and hashes and no content at all, so a
	/// signature demanding text the write does not store would oblige the pipeline to keep the whole
	/// corpus in memory purely to satisfy it. The narrower types are the guard: there is no field
	/// here to put a corpus in.
	/// </para>
	/// </summary>
	public IndexWriteSummary Upsert(
		String databasePath,
		IReadOnlyList<ScannedFile> files,
		IReadOnlyDictionary<String, IReadOnlyList<ChunkMetadata>> chunksByFile,
		IReadOnlyDictionary<String, EmbeddingResult> embeddingsByChunk,
		ModelDescriptor descriptor,
		String? fitArtifactJson = null)
	{
		ArgumentNullException.ThrowIfNull(files);
		ArgumentNullException.ThrowIfNull(chunksByFile);
		ArgumentNullException.ThrowIfNull(embeddingsByChunk);
		ArgumentNullException.ThrowIfNull(descriptor);

		String modelVersionId = descriptor.ModelVersionId;

		// foreign_keys is per connection in SQLite, not per database, so what the bootstrap set does
		// not carry here — and the deletions below rely on the cascade. The connection factory sets
		// it, along with the busy timeout this call site never had.
		using SqliteConnection connection = FolderDatabaseConnection.OpenWrite(databasePath);

		using SqliteTransaction transaction = connection.BeginTransaction();

		UpsertModel(connection, transaction, descriptor);

		// Written in the same transaction as the vectors it produced. An artifact that disagrees with
		// the stored vectors corrupts every query embedded against it, and nothing would report that.
		if (fitArtifactJson is not null)
		{
			UpsertFitArtifact(connection, transaction, modelVersionId, fitArtifactJson);
		}

		// Before the upserts, matching the order the rest of the pass assumes: rows for files that are
		// gone leave first, taking their chunks and vectors with them.
		Int32 deletedCount = this.DeleteRemovedFiles(connection, transaction, files);

		Int32 fileCount = 0;
		Int32 chunkCount = 0;
		Int32 vectorCount = 0;

		foreach (ScannedFile file in files)
		{
			UpsertFile(connection, transaction, file);
			fileCount++;

			chunksByFile.TryGetValue(file.FileId, out IReadOnlyList<ChunkMetadata>? chunks);
			chunks ??= [];

			this.DeleteSupersededChunks(connection, transaction, file.FileId, chunks);

			if (chunks.Count == 0)
			{
				continue;
			}

			foreach (ChunkMetadata chunk in chunks)
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

		return new IndexWriteSummary(fileCount, chunkCount, vectorCount, deletedCount);
	}

	/// <summary>
	/// Removes chunk rows for a file that the current scan no longer produces.
	///
	/// <para>
	/// Two cases need it. A content edit yields a new content-addressed <c>chunk_id</c> for the same
	/// <c>(file_id, chunk_index)</c> slot, which the unique constraint on that pair would otherwise
	/// reject; and a file that shrank leaves trailing chunks behind with nothing to overwrite them.
	/// Their vectors cascade away, which is what keeps every stored vector bound to the content it
	/// was computed from, under every model version.
	/// </para>
	/// </summary>
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

	/// <summary>
	/// Removes manifest rows for files that are no longer on disk; their chunks and vectors cascade
	/// away with them.
	///
	/// <para>
	/// This treats the scanned file list as the authoritative current state of the folder — which is
	/// a sharp edge worth naming: a scan that silently returned nothing would clear the index. The
	/// scanner throws on a bad path rather than returning an empty result, which is what makes this
	/// sound.
	/// </para>
	/// <para>
	/// The surviving file ids go into an indexed temp table rather than a <c>NOT IN (...)</c>
	/// parameter list. A folder of twelve thousand files would otherwise bind twelve thousand
	/// parameters, and SQLite does not build a lookup structure for a long list of *parameters* the
	/// way it does for literals — it rescans the list per row. Small test folders never expose it.
	/// </para>
	/// </summary>
	private Int32 DeleteRemovedFiles(
		SqliteConnection connection,
		SqliteTransaction transaction,
		IReadOnlyList<ScannedFile> files)
	{
		if (files.Count == 0)
		{
			this.DeleteVectorsOf(connection, transaction, "SELECT chunk_id FROM chunk_manifest;", static _ => { });

			using SqliteCommand deleteAll = connection.CreateCommand();
			deleteAll.Transaction = transaction;
			deleteAll.CommandText = "DELETE FROM file_manifest;";

			return deleteAll.ExecuteNonQuery();
		}

		PopulateScannedFileIds(connection, transaction, files);

		this.DeleteVectorsOf(
			connection,
			transaction,
			"""
			SELECT cm.chunk_id FROM chunk_manifest cm
			WHERE cm.file_id NOT IN (SELECT file_id FROM scanned_file_id);
			""",
			static _ => { });

		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = """
			DELETE FROM file_manifest
			WHERE file_id NOT IN (SELECT file_id FROM scanned_file_id);
			""";

		return command.ExecuteNonQuery();
	}

	/// <summary>
	/// Fills a temp table with the ids the current scan found. Recreated per call rather than
	/// cleared: the table is scoped to the connection, and a stale row here would spare a file that
	/// has actually been deleted.
	/// </summary>
	private static void PopulateScannedFileIds(
		SqliteConnection connection,
		SqliteTransaction transaction,
		IReadOnlyList<ScannedFile> files)
	{
		using (SqliteCommand create = connection.CreateCommand())
		{
			create.Transaction = transaction;
			create.CommandText = """
				DROP TABLE IF EXISTS temp.scanned_file_id;
				CREATE TEMP TABLE scanned_file_id (file_id TEXT PRIMARY KEY);
				""";
			create.ExecuteNonQuery();
		}

		using SqliteCommand insert = connection.CreateCommand();
		insert.Transaction = transaction;
		insert.CommandText = "INSERT OR IGNORE INTO scanned_file_id (file_id) VALUES ($fileId);";

		SqliteParameter fileId = insert.Parameters.Add("$fileId", SqliteType.Text);

		foreach (ScannedFile file in files)
		{
			fileId.Value = file.FileId;
			insert.ExecuteNonQuery();
		}
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

	private static void UpsertFile(SqliteConnection connection, SqliteTransaction transaction, ScannedFile file)
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
