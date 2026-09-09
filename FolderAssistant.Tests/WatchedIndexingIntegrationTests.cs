using FluentAssertions;
using FolderAssistant.Embedding;
using FolderAssistant.Indexing;
using FolderAssistant.Persistence;
using Microsoft.Data.Sqlite;

namespace FolderAssistant.Tests;

/// <summary>
/// The real watcher driving the real pipeline.
///
/// <para>
/// The unit tests cover the feed and the background service separately, and deliberately so — the
/// service runs against a stub feed there, which is what makes those tests fast and deterministic.
/// What none of them touches is the seam: an actual filesystem event reaching an actual re-index.
/// That is where this breaks in practice, so it is worth one slow test.
/// </para>
/// </summary>
public sealed class WatchedIndexingIntegrationTests
{
	[Fact]
	public async Task A_File_Written_To_The_Folder_Is_Indexed_Without_Anyone_Calling_The_Pipeline()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("alpha.txt"), "alpha beta gamma");

		PersistenceConfig persistence = new();
		String databasePath = new FolderDatabaseBootstrapper()
			.EnsureInitialized(folder.Path, persistence)
			.DatabasePath;

		IndexingConfig indexing = new() { ChunkSizeTokens = 8, ChunkOverlapTokens = 2 };
		FolderIndexingPipeline pipeline = new(new ProgrammableEmbeddingVectorizer("watch-test", 32));
		IndexState state = new();
		Int32 passes = 0;

		using FileSystemWatcherChangeFeed feed = new(
			folder.Path,
			persistence.MetadataFolderName,
			debounce: TimeSpan.FromMilliseconds(100),
			reconciliationInterval: TimeSpan.Zero);

		using FolderIndexingService service = new(
			() =>
			{
				Interlocked.Increment(ref passes);
				return pipeline.Run(folder.Path, databasePath, indexing);
			},
			feed,
			state);

		await service.StartAsync(CancellationToken.None);

		try
		{
			await WaitFor(() => state.Status == IndexStatus.Ready, "the initial index");
			CountIndexedFiles(databasePath).Should().Be(1);

			// Nothing here calls the pipeline. The only thing connecting this write to the index is
			// the watcher.
			await File.WriteAllTextAsync(folder.Combine("beta.txt"), "delta epsilon zeta");
			await WaitFor(() => CountIndexedFiles(databasePath) == 2, "the new file to be indexed");

			File.Delete(folder.Combine("beta.txt"));
			await WaitFor(() => CountIndexedFiles(databasePath) == 1, "the deleted file to reconcile away");

			// The load-bearing assertion, and the reason this test is worth its runtime. Every pass
			// writes the database inside the folder being watched. If those writes were not filtered
			// out, each pass would trigger the next one and indexing would never stop.
			//
			// Asserting on the file counts is not enough to catch that: a self-triggering loop
			// re-indexes the same files and leaves both the counts and the status exactly as they
			// should be, so the test passes while the folder indexes forever. Measured — with the
			// filter disabled, everything above still holds. So the assertion is that the passes
			// themselves stop.
			Int32 settled = Volatile.Read(ref passes);
			await Task.Delay(TimeSpan.FromSeconds(1));

			Volatile.Read(ref passes).Should().Be(settled, "indexing must stop once the folder does");
			state.Status.Should().Be(IndexStatus.Ready);
		}
		finally
		{
			await service.StopAsync(CancellationToken.None);
		}
	}

	private static Int64 CountIndexedFiles(String databasePath)
	{
		using SqliteConnection connection = new($"Data Source={databasePath}");
		connection.Open();

		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT COUNT(*) FROM file_manifest;";

		return (Int64)(command.ExecuteScalar() ?? 0L);
	}

	/// <summary>
	/// Polls rather than waiting on a signal, because the thing under test is the filesystem, whose
	/// event timing is not ours to control. A timeout here is the failure, and it says which step
	/// never happened rather than leaving a bare deadline.
	/// </summary>
	private static async Task WaitFor(Func<Boolean> condition, String what)
	{
		DateTime deadline = DateTime.UtcNow.AddSeconds(15);

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
