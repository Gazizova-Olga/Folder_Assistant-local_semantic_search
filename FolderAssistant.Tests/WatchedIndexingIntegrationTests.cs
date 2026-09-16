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
/// The whole path, end to end: a whole-folder pass, then the front end keeping the index in step
/// with the folder — watch, settle, record, queue, deliver, chunk, embed, store — with nothing in
/// the test calling any stage of it.
///
/// <para>
/// Every stage is tested on its own elsewhere, and deliberately so: those tests are fast and decide
/// their own timing. What none of them touches is the seams between them, an actual filesystem event
/// reaching actual vectors. That is where this breaks in practice, so it is worth one slow test.
/// </para>
/// </summary>
public sealed class WatchedIndexingIntegrationTests
{
	[Fact]
	public async Task A_File_Written_To_The_Folder_Is_Embedded_Without_Anyone_Calling_The_Pipeline()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("alpha.txt"), "alpha beta gamma");

		PersistenceConfig persistence = new();
		String databasePath = new FolderDatabaseBootstrapper()
			.EnsureInitialized(folder.Path, persistence)
			.DatabasePath;

		IndexingConfig indexing = new() { ChunkSizeTokens = 8, ChunkOverlapTokens = 2 };

		// One vectorizer and one vector store for both paths, as the composition root has them: a file
		// embeds the same way whichever path indexed it.
		ProgrammableEmbeddingVectorizer vectorizer = new("watch-test", 32);
		SqliteBlobVectorStoreWriter writer = new();
		SqliteBlobVectorStoreReader reader = new();

		FolderIndexingPipeline pipeline = new(vectorizer, writer, reader);
		FolderIndexStore store = new(databasePath);

		using RagBridgeVectorizationService bridge = new(
			folder.Path,
			databasePath,
			vectorizer,
			new FolderIndexRepository(writer),
			reader,
			TextExtractorRegistry.Default,
			indexing,
			persistence.MetadataFolderName);

		await using FolderIndexer indexer = new(
			new FolderIndexerOptions
			{
				RootPath = folder.Path,
				MetadataFolderName = persistence.MetadataFolderName,
				IndexableExtensions = TextExtractorRegistry.Default.Extensions,
				MaxContentBytes = indexing.MaxTextFileSizeBytes,
				QuietWindow = TimeSpan.FromMilliseconds(100),
				ReconciliationInterval = TimeSpan.Zero,
				SettleProbeInterval = TimeSpan.FromMilliseconds(10),
				RetryDelay = TimeSpan.FromMilliseconds(10),
				Dispatcher = new OutboxDispatcherOptions { PollInterval = TimeSpan.FromMilliseconds(50) },
			},
			store,
			store,
			bridge,
			new Sha256ContentHasher());

		IndexState state = new();

		using FolderIndexingService service = new(
			() => pipeline.Run(folder.Path, databasePath, indexing),
			indexer,
			state);

		await service.StartAsync(CancellationToken.None);

		try
		{
			await WaitFor(() => state.Status == IndexStatus.Ready, "the initial index");
			CountActiveFiles(databasePath).Should().Be(1);
			indexer.IsRunning.Should().BeTrue("the front end takes over once the pass has finished");

			// Nothing here calls the pipeline, the store or the bridge. The only thing connecting this
			// write to the vectors is the front end.
			String betaId = FileIdentity.For("beta.txt");
			await File.WriteAllTextAsync(folder.Combine("beta.txt"), "delta epsilon zeta");
			await WaitFor(() => CountVectorsOf(databasePath, betaId) > 0, "the new file to be embedded");
			CountActiveFiles(databasePath).Should().Be(2);

			File.Delete(folder.Combine("beta.txt"));
			await WaitFor(
				() => CountVectorsOf(databasePath, betaId) == 0 && CountActiveFiles(databasePath) == 1,
				"the deleted file's vectors to go");

			// The load-bearing assertion, and the reason this test is worth its runtime. Every write to
			// the database lands inside the folder being watched. If those writes were not filtered out,
			// each delivery would raise an event, which would queue another delivery, and indexing would
			// never stop — while every count above stayed exactly as it should. So the assertion is that
			// the queue itself goes quiet, and that nothing in it ever named the index's own folder.
			Int64 settled = CountOutboxOperations(databasePath);
			await Task.Delay(TimeSpan.FromSeconds(1));

			CountOutboxOperations(databasePath).Should().Be(settled, "indexing must stop once the folder does");
			OutboxPaths(databasePath).Should().NotContain(path => path.Contains(persistence.MetadataFolderName));
		}
		finally
		{
			await service.StopAsync(CancellationToken.None);
		}
	}

	private static Int64 CountActiveFiles(String databasePath)
		=> Scalar(databasePath, "SELECT COUNT(*) FROM file_manifest WHERE status = 'active';");

	private static Int64 CountVectorsOf(String databasePath, String fileId)
		=> Scalar(
			databasePath,
			"""
			SELECT COUNT(*) FROM chunk_vector cv
			JOIN chunk_manifest cm ON cm.chunk_id = cv.chunk_id
			WHERE cm.file_id = $fileId;
			""",
			("$fileId", fileId));

	private static Int64 CountOutboxOperations(String databasePath)
		=> Scalar(databasePath, "SELECT COUNT(*) FROM outbox;");

	private static IReadOnlyList<String> OutboxPaths(String databasePath)
	{
		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(databasePath);
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT file_path FROM outbox;";

		List<String> paths = [];

		using SqliteDataReader rows = command.ExecuteReader();

		while (rows.Read())
		{
			paths.Add(rows.GetString(0));
		}

		return paths;
	}

	private static Int64 Scalar(String databasePath, String sql, params (String Name, String Value)[] parameters)
	{
		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(databasePath);
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = sql;

		foreach ((String name, String value) in parameters)
		{
			command.Parameters.AddWithValue(name, value);
		}

		return (Int64)(command.ExecuteScalar() ?? 0L);
	}

	/// <summary>
	/// Polls rather than waiting on a signal, because the thing under test is the filesystem, whose
	/// event timing is not ours to control. A timeout here is the failure, and it says which step
	/// never happened rather than leaving a bare deadline.
	/// </summary>
	private static async Task WaitFor(Func<Boolean> condition, String what)
	{
		DateTime deadline = DateTime.UtcNow.AddSeconds(20);

		while (DateTime.UtcNow < deadline)
		{
			if (condition())
			{
				return;
			}

			await Task.Delay(25);
		}

		throw new TimeoutException($"Timed out waiting for {what}.");
	}
}
