using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using FolderAssistant.Embedding;
using FolderAssistant.Embedding.Lsa;
using FolderAssistant.Indexing;
using FolderAssistant.Indexing.Outbox;
using FolderAssistant.Indexing.Scanning;
using FolderAssistant.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace FolderAssistant.Tests;

/// <summary>
/// Two ways a delivery used to end in the wrong state, each driven through the real store, bridge and
/// dispatcher so that what is asserted is the outbox row the operator would see.
///
/// <para>
/// A hung embed held its operation in flight forever, with attempts at zero, so nothing retried it and
/// nothing gave up. An unfitted corpus-fitted embedder returned normally without writing, so its
/// operation was marked delivered for content that was never embedded. Both now end as a failed
/// operation carrying the error that ended it, and a file with no mark.
/// </para>
/// </summary>
public sealed class DeliveryFailureTests
{
	private const String ModelVersionId = "failure-v1";
	private const Int32 Dimension = 8;

	private static readonly IndexingConfig Config = new()
	{
		ChunkSizeTokens = 8,
		ChunkOverlapTokens = 2,
		VectorDimension = Dimension,
		ModelVersionId = ModelVersionId,
	};

	/// <summary>
	/// The plan's acceptance case for the deadline: a hung embed retires its operation as failed within
	/// the deadline, rather than leaving it in flight for the life of the process.
	/// </summary>
	[Fact]
	public async Task A_Hung_Embed_Fails_The_Delivery_Within_Its_Deadline_And_Retires_It()
	{
		using TempFolder folder = new();
		Staged staged = await StageAsync(folder);

		using OllamaEmbeddingVectorizer vectorizer = new(
			new HangingGenerator(), "qwen3-embedding:0.6b", ModelVersionId, Dimension, TimeSpan.FromMilliseconds(200));

		using RagBridgeVectorizationService bridge = BridgeOver(folder, staged, vectorizer);
		OutboxDispatcher dispatcher = new(folder.Path, staged.Store, bridge, new OutboxDispatcherOptions { MaxAttempts = 1 });

		Stopwatch elapsed = Stopwatch.StartNew();

		(await dispatcher.DrainOnceAsync(CancellationToken.None)).Should().Be(1);

		elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the deadline is what ended the delivery");

		OutboxRow op = ReadOp(staged.DatabasePath, "notes.md");
		op.Status.Should().Be(3, "an operation nothing will try again is retired as failed, never as done");
		op.Error.Should().Contain(nameof(TimeoutException));
		LastSyncedHash(staged.DatabasePath, "notes.md").Should().BeNull("nothing was delivered");
	}

	/// <summary>
	/// The plan's acceptance case for the unfitted profile: the delivery fails rather than being recorded
	/// as delivered for content that was never embedded.
	/// </summary>
	[Fact]
	public async Task An_Unfitted_Corpus_Fitted_Profile_Fails_The_Delivery_Rather_Than_Marking_It_Delivered()
	{
		using TempFolder folder = new();
		Staged staged = await StageAsync(folder);

		LsaEmbeddingVectorizer vectorizer = LsaEmbeddingVectorizer.CreateForFitting(ModelVersionId, 4);

		using RagBridgeVectorizationService bridge = BridgeOver(folder, staged, vectorizer);
		OutboxDispatcher dispatcher = new(folder.Path, staged.Store, bridge, new OutboxDispatcherOptions { MaxAttempts = 1 });

		(await dispatcher.DrainOnceAsync(CancellationToken.None)).Should().Be(1);

		OutboxRow op = ReadOp(staged.DatabasePath, "notes.md");
		op.Status.Should().Be(3);
		op.Error.Should().Contain(nameof(InvalidOperationException)).And.Contain(ModelVersionId);
		LastSyncedHash(staged.DatabasePath, "notes.md").Should().BeNull("a file believed delivered and never embedded is absent from every search");
		Scalar(staged.DatabasePath, "SELECT COUNT(*) FROM chunk_manifest;").Should().Be(0L);
	}

	private sealed record Staged(String DatabasePath, FolderIndexStore Store);

	private sealed record OutboxRow(Int64 Status, String Error);

	/// <summary>One file, recorded and queued the way the front end would.</summary>
	private static async Task<Staged> StageAsync(TempFolder folder)
	{
		Byte[] bytes = Encoding.UTF8.GetBytes("the harbour wall was rebuilt after the storm of that winter");
		await File.WriteAllBytesAsync(folder.Combine("notes.md"), bytes);

		String databasePath = new FolderDatabaseBootstrapper()
			.EnsureInitialized(folder.Path, new PersistenceConfig())
			.DatabasePath;

		FolderIndexStore store = new(databasePath);
		String hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

		await store.ApplyAsync([
			new ReconciledChange("notes.md", FileDelta.Added, new FileRecord("notes.md", hash, bytes.Length, DateTime.UtcNow)),
		]);

		return new Staged(databasePath, store);
	}

	private static RagBridgeVectorizationService BridgeOver(TempFolder folder, Staged staged, IVectorizer vectorizer)
		=> new(
			folder.Path,
			staged.DatabasePath,
			vectorizer,
			new FolderIndexRepository(new SqliteBlobVectorStoreWriter()),
			new SqliteBlobVectorStoreReader(),
			Config,
			new PersistenceConfig().MetadataFolderName);

	private static OutboxRow ReadOp(String databasePath, String relativePath)
	{
		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(databasePath);
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT status, error FROM outbox WHERE file_path = $path ORDER BY id DESC LIMIT 1;";
		command.Parameters.AddWithValue("$path", relativePath);

		using SqliteDataReader reader = command.ExecuteReader();
		reader.Read().Should().BeTrue("the delivery must have been queued");

		return new OutboxRow(reader.GetInt64(0), reader.IsDBNull(1) ? String.Empty : reader.GetString(1));
	}

	private static String? LastSyncedHash(String databasePath, String relativePath)
	{
		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(databasePath);
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT last_synced_hash FROM file_manifest WHERE file_path = $path;";
		command.Parameters.AddWithValue("$path", relativePath);

		return command.ExecuteScalar() as String;
	}

	private static Object? Scalar(String databasePath, String sql)
	{
		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(databasePath);
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = sql;

		return command.ExecuteScalar();
	}

	/// <summary>A server that never answers: the call returns only when its token is cancelled.</summary>
	private sealed class HangingGenerator : IEmbeddingGenerator<String, Embedding<Single>>
	{
		public async Task<GeneratedEmbeddings<Embedding<Single>>> GenerateAsync(
			IEnumerable<String> values,
			EmbeddingGenerationOptions? options = null,
			CancellationToken cancellationToken = default)
		{
			await Task.Delay(Timeout.Infinite, cancellationToken);

			return [];
		}

		public Object? GetService(Type serviceType, Object? serviceKey = null) => null;

		public void Dispose()
		{
		}
	}
}
