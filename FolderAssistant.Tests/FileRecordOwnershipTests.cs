using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using FolderAssistant.Embedding;
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
