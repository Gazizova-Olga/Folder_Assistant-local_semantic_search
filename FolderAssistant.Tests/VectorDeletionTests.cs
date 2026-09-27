using FluentAssertions;
using FolderAssistant.Embedding;
using FolderAssistant.Indexing;
using FolderAssistant.Indexing.Scanning;
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
///
/// <para>
/// The last two are about when that deletion happens at all: the row is ended only while it is still
/// marked removed, so a file that came back keeps both its row and its vectors.
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
		String fileId = RecordFile(databasePath, "f1.md");
		Upsert(repository, databasePath, fileId, "c1");

		// As the front end leaves it when the file goes: the row marked removed, its delivery queued.
		RecordRemoval(databasePath, "f1.md");

		writer.Deleted.Clear();

		// The delivery of the file's removal: the one write that ends a file's row, after its vectors.
		repository.DeleteFile(databasePath, fileId).Should().BeTrue();

		writer.Deleted.Should().Contain("c1");
	}

	/// <summary>
	/// A removal delivered after the file came back ends nothing — not the row the file has taken
	/// again, and not the vectors hanging from it.
	///
	/// <para>
	/// The dispatcher asks whether the file is recorded before delivering a removal, so reaching here
	/// with an active row means the file returned between that question and this write. Ended anyway,
	/// the row went and took its chunks, and the upsert queued behind the removal then found nothing
	/// recorded and skipped: a file on disk, absent from every search until a periodic pass found it.
	/// </para>
	/// </summary>
	[Fact]
	public void A_Removal_Delivered_After_The_File_Came_Back_Ends_Nothing()
	{
		using TempFolder folder = new();
		RecordingWriter writer = new();
		String databasePath = BootstrapIn(folder);

		FolderIndexRepository repository = new(writer);
		String fileId = RecordFile(databasePath, "f1.md");
		Upsert(repository, databasePath, fileId, "c1");

		RecordRemoval(databasePath, "f1.md");

		// The file comes back before its removal is delivered: the store records it active again.
		RecordFile(databasePath, "f1.md");

		writer.Deleted.Clear();

		repository.DeleteFile(databasePath, fileId).Should().BeFalse("the row is not the removed one any more");

		writer.Deleted.Should().BeEmpty("the vectors belong to the file the folder holds");
		ChunkCount(databasePath, fileId).Should().Be(1, "its chunks hang from the row it has now");
		StatusOf(databasePath, "f1.md").Should().Be("active");
	}

	/// <summary>A removal for a file no row carries ends nothing and is not an error.</summary>
	[Fact]
	public void A_Removal_For_A_File_That_Was_Never_Indexed_Ends_Nothing()
	{
		using TempFolder folder = new();
		RecordingWriter writer = new();
		String databasePath = BootstrapIn(folder);

		new FolderIndexRepository(writer)
			.DeleteFile(databasePath, FileIdentity.For("absent.md"))
			.Should()
			.BeFalse();
	}

	[Fact]
	public void Superseding_A_Files_Chunks_Deletes_The_Old_Vectors_Through_The_Store()
	{
		using TempFolder folder = new();
		RecordingWriter writer = new();
		String databasePath = BootstrapIn(folder);

		FolderIndexRepository repository = new(writer);
		String fileId = RecordFile(databasePath, "f1.md");
		Upsert(repository, databasePath, fileId, "c1");

		writer.Deleted.Clear();

		// Same file, different content: the chunk id is content-addressed, so c1 is superseded.
		Upsert(repository, databasePath, fileId, "c2");

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
		String fileId = RecordFile(databasePath, "f1.md");
		Upsert(repository, databasePath, fileId, "c1");

		writer.Deleted.Clear();

		Upsert(repository, databasePath, fileId, "c1");

		writer.Deleted.Should().BeEmpty();
	}

	/// <summary>A file's record is the store's, so it is written there first; the chunks reference it.</summary>
	private static String RecordFile(String databasePath, String relativePath)
	{
		new FolderIndexStore(databasePath)
			.ApplyAsync([new ReconciledChange(relativePath, FileDelta.Added, new FileRecord(relativePath, "hash", 10, default))])
			.GetAwaiter()
			.GetResult();

		return FileIdentity.For(relativePath);
	}

	/// <summary>Marks the row removed and queues its delivery, as the front end does when a file goes.</summary>
	private static void RecordRemoval(String databasePath, String relativePath)
		=> new FolderIndexStore(databasePath)
			.ApplyAsync([new ReconciledChange(relativePath, FileDelta.Removed, null)])
			.GetAwaiter()
			.GetResult();

	private static String StatusOf(String databasePath, String relativePath)
	{
		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(databasePath);
		using SqliteCommand command = connection.CreateCommand();

		command.CommandText = "SELECT status FROM file_manifest WHERE file_path = $path;";
		command.Parameters.AddWithValue("$path", relativePath);

		return (String?)command.ExecuteScalar() ?? "no row";
	}

	private static Int64 ChunkCount(String databasePath, String fileId)
	{
		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(databasePath);
		using SqliteCommand command = connection.CreateCommand();

		command.CommandText = "SELECT COUNT(*) FROM chunk_manifest WHERE file_id = $fileId;";
		command.Parameters.AddWithValue("$fileId", fileId);

		return (Int64)command.ExecuteScalar()!;
	}

	private static void Upsert(FolderIndexRepository repository, String databasePath, String fileId, String chunkId)
		=> repository.Upsert(
			databasePath,
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

		public Boolean RequiresVectorExtension => false;

		public void EnsureSchema(
			SqliteConnection connection,
			SqliteTransaction transaction,
			String modelVersionId,
			Int32 vectorDimension)
		{
		}

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
