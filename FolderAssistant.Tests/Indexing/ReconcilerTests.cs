using FluentAssertions;
using FolderAssistant.Indexing.Scanning;
using FolderAssistant.Indexing.Watching;
using Microsoft.Extensions.Logging;

namespace FolderAssistant.Tests.Indexing;

/// <summary>
/// The reconciler against a real folder and a fake store.
///
/// <para>
/// Half of these are about what it finds; the other half are about what it survives, and those are
/// the ones that matter. A reconciler that stops running looks exactly like one with nothing to do,
/// so the failure is silent and the symptom arrives much later as a search that does not find a
/// file that is plainly there.
/// </para>
/// </summary>
public sealed class ReconcilerTests
{
	private static readonly IndexablePathFilter Filter = new(".folderassistant");

	[Fact]
	public async Task Files_On_Disk_And_Absent_From_The_Index_Are_Added()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("a.md"), "alpha");
		await File.WriteAllTextAsync(folder.Combine("b.md"), "beta");

		FakeStore store = new();

		ReconcileResult result = await Reconcile(folder, store);

		result.Examined.Should().Be(2);
		result.Changes.Should().HaveCount(2).And.OnlyContain(change => change.Delta == FileDelta.Added);
		store.Applied.Should().HaveCount(2);
	}

	[Fact]
	public async Task A_File_Whose_Content_Changed_Is_Modified_And_One_That_Did_Not_Is_Left_Alone()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("same.md"), "unchanged");
		await File.WriteAllTextAsync(folder.Combine("edited.md"), "after");

		FakeStore store = new();
		store.Seed("same.md", await HashOf(folder.Combine("same.md")));
		store.Seed("edited.md", "a-hash-from-before-the-edit");

		ReconcileResult result = await Reconcile(folder, store);

		result.Changes.Should().ContainSingle()
			.Which.Should().Match<ReconciledChange>(c =>
				c.RelativePath == "edited.md" && c.Delta == FileDelta.Modified);
	}

	[Fact]
	public async Task A_File_In_The_Index_And_Gone_From_Disk_Is_Removed()
	{
		using TempFolder folder = new();
		FakeStore store = new();
		store.Seed("deleted.md", "some-hash");

		ReconcileResult result = await Reconcile(folder, store);

		result.Changes.Should().ContainSingle()
			.Which.Should().Match<ReconciledChange>(c =>
				c.RelativePath == "deleted.md" && c.Delta == FileDelta.Removed);
	}

	/// <summary>
	/// The pass must survive a file it cannot read. <c>Parallel.ForEachAsync</c> cancels its
	/// remaining work when a body throws, so one locked file would otherwise end the pass for every
	/// file after it — and the indexer does not own the folder it indexes, so a held file is the
	/// ordinary case rather than an error.
	/// </summary>
	[Fact]
	public async Task One_Unreadable_File_Does_Not_Stop_The_Pass()
	{
		using TempFolder folder = new();

		for (int i = 0; i < 20; i++)
		{
			await File.WriteAllTextAsync(folder.Combine($"file{i:D2}.md"), $"content {i}");
		}

		FakeStore store = new();
		ThrowingHasher hasher = new(failFor: "file07.md");

		ReconcileResult result = await Reconcile(folder, store, hasher);

		result.Skipped.Should().Be(1);
		result.Examined.Should().Be(19);
		result.Changes.Should().HaveCount(19);
		result.Changes.Should().NotContain(change => change.RelativePath == "file07.md");
	}

	/// <summary>
	/// A file that could not be hashed is left out of the pass's picture entirely, never recorded
	/// with a blank. A blank becomes the file's stored identity, so every other unhashable file
	/// carries the same one and any comparison keyed on content sees a folder of identical files.
	/// </summary>
	[Fact]
	public async Task An_Unhashable_File_Is_Never_Recorded_With_An_Empty_Hash()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("locked.md"), "held open");
		await File.WriteAllTextAsync(folder.Combine("fine.md"), "readable");

		FakeStore store = new();

		await Reconcile(folder, store, new ThrowingHasher(failFor: "locked.md"));

		store.Applied.Should().NotContain(change => change.RelativePath == "locked.md");
		store.Applied.Should().OnlyContain(change =>
			change.Current == null || !string.IsNullOrEmpty(change.Current.ContentHash));
	}

	/// <summary>
	/// A skipped file is on disk and merely unreadable — not gone. Reading its absence from the
	/// pass as a deletion would drop its index entry and re-add it next pass, an endless
	/// delete-and-restore driven purely by someone else holding it open.
	/// </summary>
	[Fact]
	public async Task A_Skipped_File_Is_Not_Mistaken_For_A_Deleted_One()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("locked.md"), "held open");

		FakeStore store = new();
		store.Seed("locked.md", "the-hash-from-last-time");

		ReconcileResult result = await Reconcile(folder, store, new ThrowingHasher(failFor: "locked.md"));

		result.Skipped.Should().Be(1);
		result.Changes.Should().BeEmpty();
		store.Applied.Should().BeEmpty();
	}

	[Fact]
	public async Task The_Metadata_Folder_Is_Not_Walked()
	{
		using TempFolder folder = new();
		Directory.CreateDirectory(folder.Combine(".folderassistant"));
		await File.WriteAllTextAsync(folder.Combine(".folderassistant", "manifest.db"), "index state");
		await File.WriteAllTextAsync(folder.Combine("real.md"), "a document");

		ReconcileResult result = await Reconcile(folder, new FakeStore());

		result.Examined.Should().Be(1);
		result.Changes.Should().ContainSingle().Which.RelativePath.Should().Be("real.md");
	}

	[Fact]
	public async Task Nested_Paths_Are_Keyed_With_Forward_Slashes_Relative_To_The_Root()
	{
		using TempFolder folder = new();
		Directory.CreateDirectory(folder.Combine("docs", "deep"));
		await File.WriteAllTextAsync(Path.Combine(folder.Combine("docs", "deep"), "note.md"), "nested");

		ReconcileResult result = await Reconcile(folder, new FakeStore());

		result.Changes.Should().ContainSingle().Which.RelativePath.Should().Be("docs/deep/note.md");
	}

	/// <summary>
	/// The recorded creation time is the file's, not the pass's. The file is backdated so that the two
	/// cannot coincide; where a platform will not set a creation time, the exact comparison with what the
	/// filesystem reports still rules out a clock reading taken during the pass.
	/// </summary>
	[Fact]
	public async Task A_New_File_Is_Recorded_With_Its_Own_Creation_Time_Not_The_Time_Of_The_Pass()
	{
		using TempFolder folder = new();
		string path = folder.Combine("aged.md");
		await File.WriteAllTextAsync(path, "written long ago");
		TimestampFixture.TryBackdate(path);

		ReconcileResult result = await Reconcile(folder, new FakeStore());

		DateTime recorded = result.Changes.Should().ContainSingle().Which.Current!.CreatedUtc;
		recorded.Should().Be(FileTimestamps.ReadCreatedUtc(new FileInfo(path)));
		TimestampFixture.ShouldBeTheBackdatedTimeWhereItTook(path, recorded);
	}

	[Fact]
	public async Task An_Edited_File_Keeps_The_Creation_Time_Already_Recorded()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("doc.md"), "after the edit");

		DateTime recordedCreation = new(2015, 6, 7, 8, 9, 10, DateTimeKind.Utc);
		FakeStore store = new();
		store.Seed("doc.md", "a-hash-from-before-the-edit", recordedCreation);

		ReconcileResult result = await Reconcile(folder, store);

		result.Changes.Should().ContainSingle()
			.Which.Current!.CreatedUtc.Should().Be(recordedCreation, "editing a file does not create it");
	}

	[Fact]
	public async Task A_Pass_That_Finds_Nothing_Does_Not_Write()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("a.md"), "alpha");

		FakeStore store = new();
		store.Seed("a.md", await HashOf(folder.Combine("a.md")));

		await Reconcile(folder, store);

		store.ApplyCalls.Should().Be(0, "an unchanged folder should cost no write at all");
	}

	/// <summary>
	/// The loop is the safety net for every event the watcher dropped. If one transient fault could
	/// end it, the index would stop converging for the rest of the process's life, and nothing would
	/// say so — the folder would simply drift.
	/// </summary>
	[Fact]
	public async Task A_Failing_Pass_Does_Not_End_The_Periodic_Loop()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("a.md"), "alpha");

		FailingStore store = new(failCalls: 3);
		Reconciler reconciler = new(folder.Path, Filter, store, new Sha256ContentHasher());

		using CancellationTokenSource stopping = new();

		Task loop = reconciler.RunPeriodicallyAsync(TimeSpan.FromMilliseconds(20), stopping.Token);

		// Wait for more passes than the store is going to reject, so the loop must have continued
		// past its own failures to get there.
		DateTime deadline = DateTime.UtcNow.AddSeconds(20);

		while (store.Calls < 6 && DateTime.UtcNow < deadline)
		{
			await Task.Delay(20);
		}

		await stopping.CancelAsync();
		await loop;

		store.Calls.Should().BeGreaterThanOrEqualTo(6, "three failed passes must not end the loop");
	}

	[Fact]
	public async Task Cancelling_Ends_The_Periodic_Loop()
	{
		using TempFolder folder = new();

		Reconciler reconciler = new(folder.Path, Filter, new FakeStore(), new Sha256ContentHasher());

		using CancellationTokenSource stopping = new();

		Task loop = reconciler.RunPeriodicallyAsync(TimeSpan.FromMilliseconds(20), stopping.Token);
		await Task.Delay(50);
		await stopping.CancelAsync();

		Func<Task> stop = async () => await loop;

		await stop.Should().CompleteWithinAsync(TimeSpan.FromSeconds(20));
	}

	/// <summary>
	/// A file somebody else is holding is the ordinary consequence of indexing a folder in use, so it
	/// is recorded at debug: enough to explain a file that never appears, quiet enough that an
	/// afternoon of editing does not bury everything else.
	/// </summary>
	[Fact]
	public async Task A_File_It_Could_Not_Hash_Is_Recorded_At_Debug_And_Nothing_Louder()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("locked.md"), "held open");
		await File.WriteAllTextAsync(folder.Combine("fine.md"), "readable");

		CapturingLogger<Reconciler> log = new();

		await new Reconciler(
			folder.Path,
			Filter,
			new FakeStore(),
			new ThrowingHasher(failFor: "locked.md"),
			logger: log).ReconcileAsync();

		log.At(LogLevel.Debug).Should().ContainSingle()
			.Which.Message.Should().Contain("locked.md");

		log.Lines.Should().OnlyContain(
			line => line.Level == LogLevel.Debug,
			"a file another process is writing is expected here, not a fault to raise");
	}

	/// <summary>
	/// Surviving a bad pass is only half of the requirement. A pass that fails every time heals nothing
	/// and leaves the folder drifting, and from outside it is indistinguishable from a pass with nothing
	/// to do — the loop is running either way. What it survived has to be recorded, or the first symptom
	/// is a search that does not find a file that is plainly there.
	/// </summary>
	[Fact]
	public async Task A_Failed_Pass_Is_Recorded_So_A_Loop_That_Heals_Nothing_Is_Not_Silent()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("a.md"), "alpha");

		FailingStore store = new(failCalls: 2);
		CapturingLogger<Reconciler> log = new();

		Reconciler reconciler = new(
			folder.Path, Filter, store, new Sha256ContentHasher(), logger: log);

		using CancellationTokenSource stopping = new();

		Task loop = reconciler.RunPeriodicallyAsync(TimeSpan.FromMilliseconds(20), stopping.Token);

		DateTime deadline = DateTime.UtcNow.AddSeconds(20);

		while (store.Calls < 4 && DateTime.UtcNow < deadline)
		{
			await Task.Delay(20);
		}

		await stopping.CancelAsync();
		await loop;

		log.At(LogLevel.Warning).Should().HaveCount(2, "one line for each pass that failed");
		log.At(LogLevel.Warning).Should().OnlyContain(
			line => line.Exception is InvalidOperationException,
			"the fault that was survived is the whole content of the report");
	}


	/// <summary>
	/// The reconciler is the one path that reaches the index without going through the debouncer, so
	/// a hold cannot reach it the way it reaches everything else. A pass landing inside one reads a
	/// file its holder is still part-way through editing and records that — an extra pass over a
	/// half-finished state, which is the whole of what the hold was opened to prevent.
	/// </summary>
	[Fact]
	public async Task A_Scheduled_Pass_Waits_While_A_Batch_Is_Held()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("a.md"), "alpha");

		FakeStore store = new();
		SwitchableHold hold = new(held: true);

		Reconciler reconciler = new(
			folder.Path, Filter, store, new Sha256ContentHasher(), hold: hold);

		using CancellationTokenSource stopping = new();

		Task loop = reconciler.RunPeriodicallyAsync(TimeSpan.FromMilliseconds(20), stopping.Token);

		// Many intervals. A pass that was merely slow would have run a dozen times by now, so
		// nothing having run says the hold stopped it rather than that the test was impatient.
		await Task.Delay(500);

		store.ReadAllCalls.Should().Be(0, "a pass inside a hold would index what its holder is still editing");

		hold.Release();

		DateTime deadline = DateTime.UtcNow.AddSeconds(20);

		while (store.ReadAllCalls == 0 && DateTime.UtcNow < deadline)
		{
			await Task.Delay(20);
		}

		await stopping.CancelAsync();
		await loop;

		store.ReadAllCalls.Should().BeGreaterThan(0, "releasing the hold lets the safety net run again");
	}

	/// <summary>
	/// A file over the size bound is not part of the corpus: it is left out of the pass the way an
	/// unreported path is, not the way an unreadable file is, so a record the index holds for it is
	/// removed. A file that grew past the bound leaves the index rather than staying stale in it.
	/// </summary>
	[Fact]
	public async Task A_File_Over_The_Size_Bound_Is_Not_Recorded_And_Leaves_The_Index_If_It_Was()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("grown.md"), "now eleven.");
		await File.WriteAllTextAsync(folder.Combine("small.md"), "tiny");

		FakeStore store = new();
		store.Seed("grown.md", "hash-of-the-smaller-version");

		Reconciler reconciler = new(
			folder.Path,
			new IndexablePathFilter(".folderassistant", maxContentBytes: 10),
			store,
			new Sha256ContentHasher());

		ReconcileResult result = await reconciler.ReconcileAsync();

		result.Changes.Should().ContainSingle(change => change.RelativePath == "grown.md")
			.Which.Delta.Should().Be(FileDelta.Removed);
		result.Changes.Should().ContainSingle(change => change.RelativePath == "small.md")
			.Which.Delta.Should().Be(FileDelta.Added);
		result.Skipped.Should().Be(0, "an oversize file is not an unreadable one");
	}

	/// <summary>
	/// An extension outside the list is not hashed at all, not merely refused afterwards: the pass
	/// repeats on every interval, and hashing every binary in the folder each time is the cost the list
	/// exists to avoid.
	/// </summary>
	[Fact]
	public async Task A_File_Outside_The_Extension_List_Is_Neither_Hashed_Nor_Recorded()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("notes.md"), "text");
		await File.WriteAllBytesAsync(folder.Combine("photo.png"), [0x89, 0x50, 0x4E, 0x47]);

		FakeStore store = new();
		CountingHasher hasher = new();

		Reconciler reconciler = new(
			folder.Path,
			new IndexablePathFilter(".folderassistant", indexableExtensions: [".md"]),
			store,
			hasher);

		ReconcileResult result = await reconciler.ReconcileAsync();

		result.Changes.Should().ContainSingle().Which.RelativePath.Should().Be("notes.md");
		hasher.Hashed.Should().ContainSingle().Which.Should().Be(folder.Combine("notes.md"));
	}

	private static Task<ReconcileResult> Reconcile(TempFolder folder, IIndexStore store, IContentHasher? hasher = null)
		=> new Reconciler(folder.Path, Filter, store, hasher ?? new Sha256ContentHasher())
			.ReconcileAsync();

	private static Task<string> HashOf(string path) => new Sha256ContentHasher().HashAsync(path);


	/// <summary>
	/// A hold the test controls. The real one expires on a clock; what matters here is only that
	/// the reconciler asks and obeys, so the answer is a field rather than a duration.
	/// </summary>
	private sealed class SwitchableHold(bool held) : IBatchHoldState
	{
		private volatile bool _held = held;

		public bool IsHoldActive => _held;

		public void Release() => _held = false;
	}

	private sealed class FakeStore : IIndexStore
	{
		private readonly Dictionary<string, FileRecord> _records = new(StringComparer.Ordinal);

		public List<ReconciledChange> Applied { get; } = [];

		public int ApplyCalls { get; private set; }

		public int ReadAllCalls { get; private set; }

		public void Seed(string relativePath, string contentHash, DateTime createdUtc = default)
			=> _records[relativePath] = new FileRecord(relativePath, contentHash, 0, createdUtc);

		public Task<IReadOnlyDictionary<string, FileRecord>> ReadAllAsync(CancellationToken cancellationToken = default)
		{
			ReadAllCalls++;

			return Task.FromResult<IReadOnlyDictionary<string, FileRecord>>(_records);
		}

		public Task<FileRecord?> ReadAsync(string relativePath, CancellationToken cancellationToken = default)
			=> Task.FromResult(_records.GetValueOrDefault(relativePath));

		public Task ApplyAsync(IReadOnlyList<ReconciledChange> changes, CancellationToken cancellationToken = default)
		{
			ApplyCalls++;
			Applied.AddRange(changes);

			return Task.CompletedTask;
		}
	}

	private sealed class FailingStore(int failCalls) : IIndexStore
	{
		public int Calls { get; private set; }

		public Task<IReadOnlyDictionary<string, FileRecord>> ReadAllAsync(CancellationToken cancellationToken = default)
		{
			Calls++;

			return Calls <= failCalls
				? Task.FromException<IReadOnlyDictionary<string, FileRecord>>(new InvalidOperationException("the store is unavailable"))
				: Task.FromResult<IReadOnlyDictionary<string, FileRecord>>(
					new Dictionary<string, FileRecord>(StringComparer.Ordinal));
		}

		public Task<FileRecord?> ReadAsync(string relativePath, CancellationToken cancellationToken = default)
			=> Task.FromResult<FileRecord?>(null);

		public Task ApplyAsync(IReadOnlyList<ReconciledChange> changes, CancellationToken cancellationToken = default)
			=> Task.CompletedTask;
	}

	/// <summary>Hashes normally and remembers which files it was asked about.</summary>
	private sealed class CountingHasher : IContentHasher
	{
		private readonly Sha256ContentHasher _inner = new();

		public List<string> Hashed { get; } = [];

		public Task<string> HashAsync(string absolutePath, CancellationToken cancellationToken = default)
		{
			lock (Hashed)
			{
				Hashed.Add(absolutePath);
			}

			return _inner.HashAsync(absolutePath, cancellationToken);
		}
	}

	/// <summary>
	/// Fails for one named file the way a live writer does, and reads the rest normally. A real lock
	/// would need a second thread holding a handle, which asserts the operating system's behaviour
	/// alongside the reconciler's.
	/// </summary>
	private sealed class ThrowingHasher(string failFor) : IContentHasher
	{
		private readonly Sha256ContentHasher _inner = new();

		public Task<string> HashAsync(string absolutePath, CancellationToken cancellationToken = default)
			=> Path.GetFileName(absolutePath) == failFor
				? throw new IOException($"The process cannot access the file '{failFor}'.")
				: _inner.HashAsync(absolutePath, cancellationToken);
	}
}
