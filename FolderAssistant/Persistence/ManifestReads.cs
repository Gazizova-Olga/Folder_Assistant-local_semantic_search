using Microsoft.Data.Sqlite;

namespace FolderAssistant.Persistence;

/// <summary>
/// The reads that are the same whatever backend holds the vectors: where a chunk's text came from,
/// and the fit artifact belonging to a model version. Both come out of <c>chunk_manifest</c>,
/// <c>file_manifest</c> and <c>embedding_fit_artifact</c> — tables every vector store shares,
/// because they describe the corpus rather than the vectors.
///
/// <para>
/// This exists so that no vector store has to reach into another one to borrow them. `SPEC-150`
/// forbids an implementation depending on a concrete implementation, and the assembly boundary that
/// would have enforced that mechanically was deliberately not built, so it holds by construction
/// instead. Two implementations legitimately sharing code is evidence of a third thing, not of a
/// dependency between them.
/// </para>
/// </summary>
internal static class ManifestReads
{
	public static IReadOnlyDictionary<String, ChunkLocation> ReadChunkLocations(
		String databasePath,
		IReadOnlyList<String> chunkIds)
	{
		ArgumentNullException.ThrowIfNull(chunkIds);

		Dictionary<String, ChunkLocation> locations = new(StringComparer.Ordinal);

		if (chunkIds.Count == 0)
		{
			return locations;
		}

		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(databasePath);
		using SqliteCommand command = connection.CreateCommand();

		// A bound parameter per id is fine here and not in DeleteRemovedFiles, because this list is
		// TopK long — five, not twelve thousand.
		String[] parameters = new String[chunkIds.Count];

		for (Int32 i = 0; i < chunkIds.Count; i++)
		{
			parameters[i] = $"$id{i}";
			command.Parameters.AddWithValue(parameters[i], chunkIds[i]);
		}

		command.CommandText = $"""
			SELECT cm.chunk_id, fm.file_path, cm.chunk_index, cm.token_start, cm.token_end
			FROM chunk_manifest cm
			JOIN file_manifest fm ON fm.file_id = cm.file_id
			WHERE cm.chunk_id IN ({String.Join(", ", parameters)});
			""";

		using SqliteDataReader reader = command.ExecuteReader();

		while (reader.Read())
		{
			String chunkId = reader.GetString(0);

			locations[chunkId] = new ChunkLocation(
				ChunkId: chunkId,
				FilePath: reader.GetString(1),
				ChunkIndex: reader.GetInt32(2),
				TokenStart: reader.GetInt32(3),
				TokenEnd: reader.GetInt32(4));
		}

		return locations;
	}

	public static String? ReadFitArtifact(String databasePath, String modelVersionId)
	{
		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(databasePath);
		using SqliteCommand command = connection.CreateCommand();

		command.CommandText = "SELECT artifact_json FROM embedding_fit_artifact WHERE model_version_id = $id;";
		command.Parameters.AddWithValue("$id", modelVersionId);

		return command.ExecuteScalar() as String;
	}
}
