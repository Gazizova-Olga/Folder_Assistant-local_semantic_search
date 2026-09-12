using System.Collections.Concurrent;
using FluentAssertions;
using FolderAssistant;
using FolderAssistant.Indexing;
using FolderAssistant.Indexing.Outbox;
using FolderAssistant.Indexing.Scanning;
using FolderAssistant.Persistence;

namespace FolderAssistant.Tests.Indexing;

/// <summary>
/// The loops run together, against a real store and a real folder.
///
/// <para>
/// Each component is tested on its own elsewhere. What is left for this class is what only the
/// composition decides: that a pass runs before anything else, that a change reaches the embedding side
/// through the loops with nobody calling them, that stopping records what was observed rather than
/// dropping it, and that the hold the watcher keeps is the one the reconciler asks about.
/// </para>
/// </summary>
public sealed class FolderIndexerTests
{
	[Fact]
	public async Task Starting_Records_The_Folder_And_Delivers_What_It_Found()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("notes.md"), "already here when indexing started");

		RecordingVectorizer vectorizer = new();
		(FolderIndexStore store, FolderIndexer indexer) = Compose(folder, vectorizer);

		await using (indexer)
		{
			await indexer.StartAsync();

			indexer.IsRunning.Should().BeTrue();

			await Until(() => vectorizer.Upserts.Contains(FileIdentity.For("notes.md")));
			(await store.ReadAsync("notes.md")).Should().NotBeNull();

			await indexer.StopAsync();

			indexer.IsRunning.Should().BeFalse();
		}

		// The database lives inside the folder being indexed, and every write to it while the loops ran
		// was a change under the watched tree. None of them may have been delivered.
		vectorizer.Upserts.Should().OnlyContain(id => id == FileIdentity.For("notes.md"),
			"the index's own writes are not part of the corpus");
	}

	[Fact]
	public async Task A_File_Written_While_Running_Is_Delivered_Without_A_Reconciliation_Pass()
	{
		using TempFolder folder = new();
		RecordingVectorizer vectorizer = new();
		(_, FolderIndexer indexer) = Compose(folder, vectorizer);

		await using (indexer)
		{
			await indexer.StartAsync();
			await LetTheWatcherAttachAsync();

			// Nothing here calls any loop. The only thing connecting this write to the delivery is the
			// composition.
			await File.WriteAllTextAsync(folder.Combine("later.md"), "written after the start");

			await Until(() => vectorizer.Upserts.Contains(FileIdentity.For("later.md")));
		}
	}

	/// <summary>
	/// The order of stopping is the guarantee. The change is reported and then the indexer stopped
	/// within the same quiet window, so it has settled nowhere by the time the stop begins; a stop that
	/// cancelled the loops first would drop it, and the file would stay unrecorded until a later pass
	/// happened to notice.
	/// </summary>
	[Fact]
	public async Task Stopping_Records_A_Change_Still_Inside_Its_Quiet_Window()
	{
		using TempFolder folder = new();
		(FolderIndexStore store, FolderIndexer indexer) = Compose(
			folder, new RecordingVectorizer(), options => options with { QuietWindow = TimeSpan.FromSeconds(30) });

		await using (indexer)
		{
			await indexer.StartAsync();

			string path = folder.Combine("late.md");
			await File.WriteAllTextAsync(path, "written moments before the stop");

			// Reported rather than left to the operating system, so the change is in the debouncer for
			// certain when the stop begins; an event still in flight would make this a race.
			await indexer.NotifyCreatedAsync(path);

			await indexer.StopAsync();
		}

		(await store.ReadAsync("late.md")).Should().NotBeNull("a change observed before the stop is recorded, not dropped");
	}

	/// <summary>
	/// The reconciler is the one path that reaches the store without passing through the debouncer, and
	/// it only defers for a hold it was handed. This is the test that fails if the composition builds the
	/// two without introducing them: the quiet window is long enough that only a periodic pass could
	/// record the file, and it must not while the hold is open.
	/// </summary>
	[Fact]
	public async Task A_Periodic_Pass_Waits_For_A_Hold_Opened_On_The_Indexer()
	{
		using TempFolder folder = new();
		(FolderIndexStore store, FolderIndexer indexer) = Compose(
			folder,
			new RecordingVectorizer(),
			options => options with
			{
				QuietWindow = TimeSpan.FromSeconds(30),
				ReconciliationInterval = TimeSpan.FromMilliseconds(100),
			});

		await using (indexer)
		{
			await indexer.StartAsync();

			using (indexer.BeginBatch())
			{
				await File.WriteAllTextAsync(folder.Combine("held.md"), "part of an edit still going on");

				// Several intervals, so "not recorded" is not "not recorded yet".
				await Task.Delay(TimeSpan.FromMilliseconds(600));

				(await store.ReadAsync("held.md")).Should().BeNull("a pass landing inside a hold would record a half-finished edit");
			}

			await Until(() => store.ReadAsync("held.md").Result is not null);
		}
	}

	[Fact]
	public async Task Starting_Twice_Is_Refused()
	{
		using TempFolder folder = new();
		(_, FolderIndexer indexer) = Compose(folder, new RecordingVectorizer());

		await using (indexer)
		{
			await indexer.StartAsync();

			Func<Task> again = () => indexer.StartAsync();

			await again.Should().ThrowAsync<InvalidOperationException>();
		}
	}

	/// <summary>
	/// A stop with nothing running does nothing, and so do a report and a hold: a change made while
	/// nothing is watching is what the pass at the next start is for.
	/// </summary>
	[Fact]
	public async Task While_Nothing_Is_Running_Reports_And_Holds_Are_Accepted_And_Do_Nothing()
	{
		using TempFolder folder = new();
		(FolderIndexStore store, FolderIndexer indexer) = Compose(folder, new RecordingVectorizer());

		await using (indexer)
		{
			await indexer.StopAsync();

			indexer.IsRunning.Should().BeFalse();

			string path = folder.Combine("unwatched.md");
			await File.WriteAllTextAsync(path, "nobody is watching");

			using (indexer.BeginBatch())
			{
				await indexer.NotifyCreatedAsync(path);
			}

			(await store.ReadAsync("unwatched.md")).Should().BeNull();
		}
	}

	private static (FolderIndexStore Store, FolderIndexer Indexer) Compose(
		TempFolder folder,
		IVectorizationService vectorizer,
		Func<FolderIndexerOptions, FolderIndexerOptions>? configure = null)
	{
		DatabaseBootstrapResult database = new FolderDatabaseBootstrapper()
			.EnsureInitialized(folder.Path, new PersistenceConfig());

		FolderIndexStore store = new(database.DatabasePath);

		// Fast everywhere a real deployment would wait, so the tests wait on the machine and not on a
		// schedule; no periodic pass unless a test asks for one, so what it observes came through the
		// watcher.
		FolderIndexerOptions options = new()
		{
			RootPath = folder.Path,
			MetadataFolderName = ".folderassistant",
			QuietWindow = TimeSpan.FromMilliseconds(100),
			ReconciliationInterval = TimeSpan.Zero,
			SettleProbeInterval = TimeSpan.FromMilliseconds(10),
			RetryDelay = TimeSpan.FromMilliseconds(10),
			Dispatcher = new OutboxDispatcherOptions { PollInterval = TimeSpan.FromMilliseconds(50) },
		};

		options = configure?.Invoke(options) ?? options;

		return (store, new FolderIndexer(options, store, store, vectorizer, new Sha256ContentHasher()));
	}

	/// <summary>The watcher attaches synchronously, but the operating system takes a moment to start delivering.</summary>
	private static Task LetTheWatcherAttachAsync() => Task.Delay(200);

	/// <summary>Waits for <paramref name="condition"/>, failing if it has not held within a generous deadline.</summary>
	private static async Task Until(Func<bool> condition)
	{
		DateTime deadline = DateTime.UtcNow.AddSeconds(20);

		while (!condition() && DateTime.UtcNow < deadline)
		{
			await Task.Delay(10);
		}

		condition().Should().BeTrue("it should have happened well within the deadline");
	}

	/// <summary>An embedding side that remembers what it was handed.</summary>
	private sealed class RecordingVectorizer : IVectorizationService
	{
		public ConcurrentBag<string> Upserts { get; } = [];

		public ConcurrentBag<string> Deletes { get; } = [];

		public Task UpsertAsync(string docId, Stream content, FileMetadata metadata, CancellationToken cancellationToken)
		{
			Upserts.Add(docId);

			return Task.CompletedTask;
		}

		public Task DeleteAsync(string docId, CancellationToken cancellationToken)
		{
			Deletes.Add(docId);

			return Task.CompletedTask;
		}
	}
}
