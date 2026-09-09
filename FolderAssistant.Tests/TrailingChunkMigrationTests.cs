using FluentAssertions;
using FolderAssistant.Embedding;
using FolderAssistant.Indexing;
using FolderAssistant.Persistence;
using Microsoft.Data.Sqlite;

namespace FolderAssistant.Tests;

/// <summary>
/// A folder indexed before redundant trailing windows were dropped still has that extra chunk row,
/// and its vector, sitting on disk. SPEC-120 claims they reconcile away on the next pass without
/// re-embedding anything. This checks the claim rather than trusting it: a stale chunk that
/// survived would go on returning duplicate text from retrieval indefinitely, on exactly the
/// databases nobody thinks to rebuild.
/// </summary>
public sealed class TrailingChunkMigrationTests
{
	[Fact]
	public void A_Stale_Trailing_Chunk_Is_Reconciled_Away_Without_Re_Embedding_The_File()
	{
		using TempFolder folder = new();
		File.WriteAllText(folder.Combine("doc.txt"), "a b c d e f g");

		String databasePath = new FolderDatabaseBootstrapper()
			.EnsureInitialized(folder.Path, new PersistenceConfig())
			.DatabasePath;

		IndexingConfig config = new()
		{
			ChunkSizeTokens = 4,
			ChunkOverlapTokens = 2,
			ModelVersionId = "migration-v1",
		};

		FolderIndexingPipeline pipeline = new(new ProgrammableEmbeddingVectorizer("migration-v1", 32));

		IndexingResult first = pipeline.Run(folder.Path, databasePath, config);
		first.ChunksIndexed.Should().Be(3);

		// Stands in for the old behaviour: the fourth chunk — "g", wholly contained in chunk 2 —
		// together with the vector it would have been given.
		String staleChunkId = PlantStaleTrailingChunk(databasePath, "migration-v1");
		CountRows(databasePath, "chunk_manifest").Should().Be(4);
		CountRows(databasePath, "chunk_vector").Should().Be(4);

		// The file has not changed, so this is the path that matters — the one where the pipeline
		// skips embedding entirely. If reconciliation only ran for re-embedded files, the stale
		// chunk would outlive every future pass.
		IndexingResult second = pipeline.Run(folder.Path, databasePath, config);

		second.FilesIndexed.Should().Be(0);
		second.FilesUnchanged.Should().Be(1);

		CountRows(databasePath, "chunk_manifest").Should().Be(3);
		CountRows(databasePath, "chunk_vector").Should().Be(3);
		ChunkExists(databasePath, staleChunkId).Should().BeFalse();
	}

	private static String PlantStaleTrailingChunk(String databasePath, String modelVersionId)
	{
		const String StaleChunkId = "stale-trailing-chunk";

		using SqliteConnection connection = FolderDatabaseConnection.OpenWrite(databasePath);

		using SqliteCommand read = connection.CreateCommand();
		read.CommandText = "SELECT file_id FROM file_manifest LIMIT 1;";
		String fileId = (String)read.ExecuteScalar()!;

		using SqliteCommand insertChunk = connection.CreateCommand();
		insertChunk.CommandText = """
			INSERT INTO chunk_manifest
				(chunk_id, file_id, chunk_index, token_start, token_end, chunk_hash, model_version_id, updated_utc)
			VALUES ($chunkId, $fileId, 3, 6, 7, 'stale-hash', $modelVersionId, $now);
			""";
		insertChunk.Parameters.AddWithValue("$chunkId", StaleChunkId);
		insertChunk.Parameters.AddWithValue("$fileId", fileId);
		insertChunk.Parameters.AddWithValue("$modelVersionId", modelVersionId);
		insertChunk.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
		insertChunk.ExecuteNonQuery();

		using SqliteCommand insertVector = connection.CreateCommand();
		insertVector.CommandText = """
			INSERT INTO chunk_vector
				(chunk_id, model_version_id, vector, vector_dimension, updated_utc)
			VALUES ($chunkId, $modelVersionId, X'00000000', 1, $now);
			""";
		insertVector.Parameters.AddWithValue("$chunkId", StaleChunkId);
		insertVector.Parameters.AddWithValue("$modelVersionId", modelVersionId);
		insertVector.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
		insertVector.ExecuteNonQuery();

		return StaleChunkId;
	}

	private static Int64 CountRows(String databasePath, String table)
	{
		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(databasePath);
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = $"SELECT COUNT(*) FROM {table};";

		return (Int64)(command.ExecuteScalar() ?? 0L);
	}

	private static Boolean ChunkExists(String databasePath, String chunkId)
	{
		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(databasePath);
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT COUNT(*) FROM chunk_manifest WHERE chunk_id = $chunkId;";
		command.Parameters.AddWithValue("$chunkId", chunkId);

		return (Int64)(command.ExecuteScalar() ?? 0L) > 0;
	}
}
