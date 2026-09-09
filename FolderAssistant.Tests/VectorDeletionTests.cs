using FluentAssertions;
using FolderAssistant.Embedding;
using FolderAssistant.Indexing;
using FolderAssistant.Persistence;
using Microsoft.Data.Sqlite;

namespace FolderAssistant.Tests;

/// <summary>
/// Vectors are deleted through <see cref="IVectorStoreWriter.DeleteVectors"/> rather than left to
/// the <c>chunk_manifest</c> cascade.
///
/// <para>
/// **These assert on the call, not on the row count, and that is the whole point.** The cascade
/// still fires on this backend, so a row-count assertion stays green with the explicit delete
/// removed entirely — it would be a test that looks like a guard and is not. What is under test is
/// that deletion goes through the contract, because a native vector extension stores vectors in a
/// virtual table, a virtual table cannot be a foreign-key target, and such a backend could
/// therefore never provide the cascade this backend happens to have.
/// </para>
/// </summary>
public sealed class VectorDeletionTests
{
	[Fact]
	public void Removing_A_File_Deletes_Its_Vectors_Through_The_Store()
	{
		using TempFolder folder = new();
		RecordingWriter writer = new();
		String databasePath = BootstrapIn(folder);

		FolderIndexRepository repository = new(writer);
		Upsert(repository, databasePath, "f1", "c1");

		writer.Deleted.Clear();

		// A pass that scans nothing: the file is gone from disk.
		repository.Upsert(
			databasePath,
			[],
			new Dictionary<String, IReadOnlyList<ChunkMetadata>>(),
			new Dictionary<String, EmbeddingResult>(),
			Descriptor());

		writer.Deleted.Should().Contain("c1");
	}

	[Fact]
	public void Superseding_A_Files_Chunks_Deletes_The_Old_Vectors_Through_The_Store()
	{
		using TempFolder folder = new();
		RecordingWriter writer = new();
		String databasePath = BootstrapIn(folder);

		FolderIndexRepository repository = new(writer);
		Upsert(repository, databasePath, "f1", "c1");

		writer.Deleted.Clear();

		// Same file, different content: the chunk id is content-addressed, so c1 is superseded.
		Upsert(repository, databasePath, "f1", "c2");

		writer.Deleted.Should().Contain("c1");
		writer.Deleted.Should().NotContain("c2");
	}

	/// <summary>A file whose chunks are unchanged has nothing to delete, so nothing is asked for.</summary>
	[Fact]
	public void An_Unchanged_File_Deletes_No_Vectors()
	{
		using TempFolder folder = new();
		RecordingWriter writer = new();
		String databasePath = BootstrapIn(folder);

		FolderIndexRepository repository = new(writer);
		Upsert(repository, databasePath, "f1", "c1");

		writer.Deleted.Clear();

		Upsert(repository, databasePath, "f1", "c1");

		writer.Deleted.Should().BeEmpty();
	}

	private static void Upsert(FolderIndexRepository repository, String databasePath, String fileId, String chunkId)
		=> repository.Upsert(
			databasePath,
			[new ScannedTextFile(fileId, $"/tmp/{fileId}.md", $"{fileId}.md", $"hash-{chunkId}", 10,
				DateTime.UtcNow, "alpha beta", "md").ToMetadata()],
			new Dictionary<String, IReadOnlyList<ChunkMetadata>>
			{
				[fileId] = [new TextChunk(chunkId, 0, 0, 2, $"chash-{chunkId}", "alpha beta").ToMetadata()],
			},
			new Dictionary<String, EmbeddingResult>
			{
				[chunkId] = new([1f, 0f], "m1", 2, "stub"),
			},
			Descriptor());

	private static ModelDescriptor Descriptor() => new("m1", "stub", "stub-model", 2);

	private static String BootstrapIn(TempFolder folder)
		=> new FolderDatabaseBootstrapper().EnsureInitialized(folder.Path, new PersistenceConfig()).DatabasePath;

	/// <summary>Writes for real, and records which chunk ids deletion was asked for.</summary>
	private sealed class RecordingWriter : IVectorStoreWriter
	{
		private readonly SqliteBlobVectorStoreWriter _inner = new();

		public List<String> Deleted { get; } = [];

		public void UpsertVector(
			SqliteConnection connection,
			SqliteTransaction transaction,
			String chunkId,
			String modelVersionId,
			IReadOnlyList<Single> vector,
			Int32 vectorDimension)
			=> this._inner.UpsertVector(connection, transaction, chunkId, modelVersionId, vector, vectorDimension);

		public void DeleteVectors(
			SqliteConnection connection,
			SqliteTransaction transaction,
			IReadOnlyList<String> chunkIds)
		{
			this.Deleted.AddRange(chunkIds);
			this._inner.DeleteVectors(connection, transaction, chunkIds);
		}
	}
}
