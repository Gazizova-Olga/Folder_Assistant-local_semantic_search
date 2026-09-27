using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using FolderAssistant.Embedding;
using FolderAssistant.Extraction;
using FolderAssistant.Indexing;
using FolderAssistant.Indexing.Outbox;
using FolderAssistant.Indexing.Scanning;
using FolderAssistant.Persistence;
using Microsoft.Data.Sqlite;

namespace FolderAssistant.Tests;

/// <summary>
/// One writer of what a file's record says: the store. The whole-folder pass and the per-file delivery
/// write chunks and vectors under a row the store wrote, and neither states anything about the row.
///
/// <para>
/// The defect this pins was silent. A delivery read a file's hash, embedded, and wrote the hash back —
/// reverting a newer one the front end had recorded during the embed. Its own conditional mark then
/// matched the reverted row, the delivery queued for the newer content found its work already done
/// and skipped, and the file sat on stale vectors until a periodic pass happened to re-hash it. Every
/// count looked right throughout.
/// </para>
/// </summary>
public sealed class FileRecordOwnershipTests
{
	private const String ModelVersionId = "ownership-v1";
	private const Int32 Dimension = 32;

	private static readonly IndexingConfig Config = new()
	{
		ChunkSizeTokens = 8,
		ChunkOverlapTokens = 2,
		VectorDimension = Dimension,
		ModelVersionId = ModelVersionId,
	};

	/// <summary>
	/// A file whose record moves on while its delivery is in flight is embedded at the newer content
	/// by the delivery already queued for it — not by a reconciliation minutes later.
	///
	/// <para>
	/// The delivery holds the file open share-Read, which denies a writer on Windows, so the newer bytes
	/// reach the disk only once its handle closes. What the old code could revert was the record, and
	/// the record is what moves here while the embed is held: exactly what the front end writes when a
	/// settled change lands mid-delivery.
	/// </para>
	/// </summary>
	[Fact]
	public async Task An_Edit_Recorded_During_A_Delivery_Is_Embedded_By_The_Delivery_Queued_For_It()
	{
		using TempFolder folder = new();
		String path = folder.Combine("notes.md");
		String databasePath = Bootstrap(folder);

		Byte[] before = Encoding.UTF8.GetBytes("the harbour wall was rebuilt after the storm of that winter");
		Byte[] after = Encoding.UTF8.GetBytes("the lighthouse keeper logged every ship that passed the point");
		String hashBefore = Hash(before);
		String hashAfter = Hash(after);

		await File.WriteAllBytesAsync(path, before);

		FolderIndexStore store = new(databasePath);
		await store.ApplyAsync([Change("notes.md", FileDelta.Added, hashBefore, before.Length)]);

		using GatedVectorizer vectorizer = new(new ProgrammableEmbeddingVectorizer(ModelVersionId, Dimension));

		using RagBridgeVectorizationService bridge = new(
			folder.Path,
			databasePath,
			vectorizer,
			new FolderIndexRepository(new SqliteBlobVectorStoreWriter()),
			new SqliteBlobVectorStoreReader(),
			TextExtractorRegistry.Default,
			Config,
			new PersistenceConfig().MetadataFolderName);

		OutboxDispatcher dispatcher = new(folder.Path, store, bridge);

		// The first delivery reads the record at the old hash and stops inside the embed.
		Task<Int32> first = dispatcher.DrainOnceAsync(CancellationToken.None);
		(await vectorizer.Entered.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue("the delivery reached the embed");

		// The front end records the edit while the embed is held, and queues the delivery for it.
		await store.ApplyAsync([Change("notes.md", FileDelta.Modified, hashAfter, after.Length)]);

		vectorizer.Release.Release();
		(await first).Should().Be(1);

		await File.WriteAllBytesAsync(path, after);

		Row row = ReadRow(databasePath, "notes.md");
		row.FileHash.Should().Be(hashAfter, "a delivery states nothing about the file's record");
		row.LastSyncedHash.Should().BeNull("what was delivered is no longer what the file holds");

		// The delivery queued for the edit does the work, with nothing else running.
		(await dispatcher.DrainOnceAsync(CancellationToken.None)).Should().Be(1);

		row = ReadRow(databasePath, "notes.md");
		row.LastSyncedHash.Should().Be(hashAfter);
		vectorizer.Texts.Should().Contain(text => text.Contains("lighthouse"), "the newer content was embedded");
	}

	/// <summary>
	/// A removal queued before the file came back must not end the row the file has since taken again.
	///
	/// <para>
	/// The store records a restored file as active with its mark cleared and queues the upsert that
	/// re-embeds it, behind the removal already queued. Delivered anyway, the removal ended the row and
	/// took the chunks with it; the upsert then found nothing recorded and skipped, and the file was
	/// absent from every search until a periodic pass rediscovered it. Plausible during a long startup
	/// backlog, and every count looked right throughout.
	/// </para>
	/// </summary>
	[Fact]
	public async Task A_Removal_Delivered_After_The_File_Came_Back_Leaves_It_Indexed()
	{
		using TempFolder folder = new();
		String path = folder.Combine("notes.md");
		String databasePath = Bootstrap(folder);

		Byte[] content = Encoding.UTF8.GetBytes("the ferry timetable changed the week the new pier opened");
		String hash = Hash(content);
		await File.WriteAllBytesAsync(path, content);

		FolderIndexStore store = new(databasePath);
		await store.ApplyAsync([Change("notes.md", FileDelta.Added, hash, content.Length)]);
		OutboxDrain.Deliver(folder.Path, databasePath, Config).Should().Be(1);

		// Removed, then back before the removal was delivered: the front end records both, in this order.
		File.Delete(path);
		await store.ApplyAsync([new ReconciledChange("notes.md", FileDelta.Removed, null)]);
		await File.WriteAllBytesAsync(path, content);
		await store.ApplyAsync([Change("notes.md", FileDelta.Added, hash, content.Length)]);

		OutboxDrain.Deliver(folder.Path, databasePath, Config).Should().Be(2, "the removal, and the upsert behind it");

		Row row = ReadRow(databasePath, "notes.md");
		row.LastSyncedHash.Should().Be(hash, "the upsert queued for the restored file embedded it");
		ChunkCount(databasePath, row.FileId).Should().BePositive("its chunks hang from the row it has now");
	}

	/// <summary>
	/// The file returning <em>after</em> the dispatcher asked whether it was back, and before the row is
	/// ended, leaves it indexed too. That is the residual window the dispatcher's own question cannot
	/// close: one store round-trip wide, and closed on the side holding the write.
	///
	/// <para>
	/// Staged by recording the file again from inside the removal's delivery, which is where a real
	/// return would land — a settled change or a reconciliation pass writing while the delivery is on
	/// its way to the database. Ended anyway, the row goes and takes the chunks of the file the folder
	/// now holds, and the upsert queued behind the removal finds nothing recorded and skips.
	/// </para>
	/// </summary>
	[Fact]
	public async Task A_File_Returning_Inside_Its_Removals_Delivery_Keeps_Its_Row_And_Chunks()
	{
		using TempFolder folder = new();
		String path = folder.Combine("notes.md");
		String databasePath = Bootstrap(folder);

		Byte[] content = Encoding.UTF8.GetBytes("the tide gauge readings were copied out by hand until the cable came");
		String hash = Hash(content);
		await File.WriteAllBytesAsync(path, content);

		FolderIndexStore store = new(databasePath);
		await store.ApplyAsync([Change("notes.md", FileDelta.Added, hash, content.Length)]);
		OutboxDrain.Deliver(folder.Path, databasePath, Config).Should().Be(1);

		String fileId = ReadRow(databasePath, "notes.md").FileId;
		ChunkCount(databasePath, fileId).Should().BePositive("the file was indexed before it was removed");

		// Removed and queued, with nothing recording the return yet: the dispatcher's question will say
		// the file is gone, which is what makes the delivery proceed as far as the write.
		File.Delete(path);
		await store.ApplyAsync([new ReconciledChange("notes.md", FileDelta.Removed, null)]);

		using RagBridgeVectorizationService bridge = new(
			folder.Path,
			databasePath,
			new ProgrammableEmbeddingVectorizer(ModelVersionId, Dimension),
			new FolderIndexRepository(new SqliteBlobVectorStoreWriter()),
			new SqliteBlobVectorStoreReader(),
			TextExtractorRegistry.Default,
			Config,
			new PersistenceConfig().MetadataFolderName);

		// The return lands inside the delivery, after the question and before the row is ended.
		ReturningOnDelete vectorization = new(bridge, async () =>
		{
			await File.WriteAllBytesAsync(path, content);
			await store.ApplyAsync([Change("notes.md", FileDelta.Added, hash, content.Length)]);
		});

		OutboxDispatcher dispatcher = new(folder.Path, store, vectorization);

		(await dispatcher.DrainOnceAsync(CancellationToken.None)).Should().Be(1, "the removal was delivered");

		Row row = ReadRow(databasePath, "notes.md");
		row.FileId.Should().Be(fileId, "the row the file took again is the row it had");
		ChunkCount(databasePath, fileId).Should().BePositive("a removal ends nothing once the file is recorded again");

		// The upsert the return queued has a row to write under, so the file is delivered rather than
		// waiting for a periodic pass to rediscover it.
		(await dispatcher.DrainOnceAsync(CancellationToken.None)).Should().Be(1);
		ReadRow(databasePath, "notes.md").LastSyncedHash.Should().Be(hash);
	}

	/// <summary>
	/// The pass records the folder through the store and then says what it embedded; it writes no row
	/// of its own. The deliveries the record queued find their work done, so a cold start costs one
	/// embed per file and not two.
	/// </summary>
	[Fact]
	public void A_Whole_Folder_Pass_Records_Through_The_Store_And_Marks_What_It_Embedded_Delivered()
	{
		using TempFolder folder = new();
		File.WriteAllText(folder.Combine("alpha.md"), "alpha beta gamma delta epsilon zeta", Encoding.UTF8);
		File.WriteAllText(folder.Combine("beta.md"), "eta theta iota kappa lambda mu", Encoding.UTF8);
		String databasePath = Bootstrap(folder);

		CountingVectorizer vectorizer = new(new ProgrammableEmbeddingVectorizer(ModelVersionId, Dimension));
		IndexingResult result = new FolderIndexingPipeline(vectorizer).Run(folder.Path, databasePath, Config);

		result.ChangesRecorded.Should().Be(2);
		result.FilesIndexed.Should().Be(2);
		result.FilesDeferred.Should().Be(0);

		foreach (String name in new[] { "alpha.md", "beta.md" })
		{
			Row row = ReadRow(databasePath, name);

			row.FileId.Should().Be(FileIdentity.For(name), "the row is the store's, under the id the library derives");
			row.CreatedUtc.Should().NotBeNull("the store records the file's own creation time; the pass never wrote one");
			row.LastSyncedHash.Should().Be(row.FileHash, "the pass reports what it embedded as delivered");
		}

		// The record queued a delivery per file. Each finds its work done: nothing is embedded twice.
		Int32 embedCalls = vectorizer.Calls;

		OutboxDrain.Deliver(folder.Path, databasePath, Config, vectorizer).Should().Be(2);
		vectorizer.Calls.Should().Be(embedCalls, "a delivery for content the pass already embedded skips");
	}

	/// <summary>
	/// The ownership is enforced by the schema, not by discipline: a chunk row references a file row,
	/// and the repository has no statement that could create one.
	/// </summary>
	[Fact]
	public void The_Repository_Refuses_Chunks_For_A_File_The_Store_Has_Not_Recorded()
	{
		using TempFolder folder = new();
		String databasePath = Bootstrap(folder);

		FluentActions.Invoking(() => new FolderIndexRepository().Upsert(
				databasePath,
				new Dictionary<String, IReadOnlyList<ChunkMetadata>> { ["nobody"] = [new ChunkMetadata("c1", 0, 0, 1, "h")] },
				new Dictionary<String, EmbeddingResult> { ["c1"] = new([1f], ModelVersionId, 1, "programmable") },
				new ModelDescriptor(ModelVersionId, "programmable", "programmable-embedding", 1)))
			.Should().Throw<SqliteException>("a chunk row references a file row only the store writes");
	}

	private static String Bootstrap(TempFolder folder)
		=> new FolderDatabaseBootstrapper().EnsureInitialized(folder.Path, new PersistenceConfig()).DatabasePath;

	/// <summary>The hash both writers record: SHA-256 over the bytes, lowercase hex.</summary>
	private static String Hash(Byte[] bytes)
		=> Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

	private static ReconciledChange Change(String path, FileDelta delta, String hash, Int64 size)
		=> new(path, delta, new FileRecord(path, hash, size, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)));

	private sealed record Row(String FileId, String FileHash, String? LastSyncedHash, String? CreatedUtc);

	private static Row ReadRow(String databasePath, String relativePath)
	{
		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(databasePath);
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText =
			"SELECT file_id, file_hash, last_synced_hash, created_utc FROM file_manifest WHERE file_path = $path;";
		command.Parameters.AddWithValue("$path", relativePath);

		using SqliteDataReader reader = command.ExecuteReader();
		reader.Read().Should().BeTrue($"{relativePath} should be recorded");

		return new Row(
			reader.GetString(0),
			reader.GetString(1),
			reader.IsDBNull(2) ? null : reader.GetString(2),
			reader.IsDBNull(3) ? null : reader.GetString(3));
	}

	private static Int64 ChunkCount(String databasePath, String fileId)
	{
		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(databasePath);
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT COUNT(*) FROM chunk_manifest WHERE file_id = $fileId;";
		command.Parameters.AddWithValue("$fileId", fileId);

		return (Int64)command.ExecuteScalar()!;
	}

	/// <summary>
	/// Runs <paramref name="onDelete"/> before passing a removal on, which is how the window between the
	/// dispatcher's question and the row being ended is staged deterministically.
	/// </summary>
	private sealed class ReturningOnDelete(IVectorizationService inner, Func<Task> onDelete) : IVectorizationService
	{
		public Task UpsertAsync(String docId, Stream content, FileMetadata metadata, CancellationToken cancellationToken)
			=> inner.UpsertAsync(docId, content, metadata, cancellationToken);

		public async Task DeleteAsync(String docId, CancellationToken cancellationToken)
		{
			await onDelete();

			await inner.DeleteAsync(docId, cancellationToken);
		}
	}

	/// <summary>Counts the embed calls it passes through.</summary>
	private sealed class CountingVectorizer(IVectorizer inner) : IVectorizer
	{
		public Int32 Calls { get; private set; }

		public ModelDescriptor Descriptor => inner.Descriptor;

		public ValueTask<IReadOnlyList<EmbeddingResult>> VectorizeAsync(
			IReadOnlyList<String> texts,
			EmbeddingKind kind,
			CancellationToken cancellationToken = default)
		{
			this.Calls++;

			return inner.VectorizeAsync(texts, kind, cancellationToken);
		}
	}

	/// <summary>
	/// Holds its first embed until released, so a test can act while a delivery is in flight, and
	/// records every text it is handed.
	/// </summary>
	private sealed class GatedVectorizer(IVectorizer inner) : IVectorizer, IDisposable
	{
		private Int32 _calls;

		public SemaphoreSlim Entered { get; } = new(0);

		public SemaphoreSlim Release { get; } = new(0);

		public List<String> Texts { get; } = [];

		public ModelDescriptor Descriptor => inner.Descriptor;

		public async ValueTask<IReadOnlyList<EmbeddingResult>> VectorizeAsync(
			IReadOnlyList<String> texts,
			EmbeddingKind kind,
			CancellationToken cancellationToken = default)
		{
			this.Texts.AddRange(texts);

			if (Interlocked.Increment(ref this._calls) == 1)
			{
				this.Entered.Release();
				await this.Release.WaitAsync(cancellationToken);
			}

			return await inner.VectorizeAsync(texts, kind, cancellationToken);
		}

		public void Dispose()
		{
			this.Entered.Dispose();
			this.Release.Dispose();
		}
	}
}
