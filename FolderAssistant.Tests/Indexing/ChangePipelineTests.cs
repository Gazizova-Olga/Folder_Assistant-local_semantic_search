using System.Threading.Channels;
using FluentAssertions;
using FolderAssistant.Indexing.Pipeline;
using Microsoft.Extensions.Logging;
using FolderAssistant.Indexing.Scanning;
using FolderAssistant.Indexing.Watching;

namespace FolderAssistant.Tests.Indexing;

/// <summary>
/// The per-change path against a real folder and a store that keeps what it is given.
///
/// <para>
/// The ones that matter are about busy files. This path reads files other processes are writing, so a
/// read that loses to a writer is ordinary — and a change dropped at that moment has nothing behind it
/// to try again, which leaves the file stale with no symptom until a reconcile passes over it.
/// </para>
/// </summary>
public sealed class ChangePipelineTests
{
	[Fact]
	public async Task A_New_File_Is_Added()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("new.md"), "fresh");

		RecordingStore store = new();

		await Run(Pipeline(folder, store), Changed(folder, "new.md", FileChangeKind.Created));

		store.Applied.Should().ContainSingle()
			.Which.Should().Match<ReconciledChange>(c =>
				c.RelativePath == "new.md" && c.Delta == FileDelta.Added && c.Current!.ContentHash.Length > 0);
	}

	[Fact]
	public async Task An_Edited_File_Is_Modified()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("doc.md"), "after the edit");

		RecordingStore store = new();
		store.Seed("doc.md", "a-hash-from-before-the-edit");

		await Run(Pipeline(folder, store), Changed(folder, "doc.md", FileChangeKind.Modified));

		store.Applied.Should().ContainSingle().Which.Delta.Should().Be(FileDelta.Modified);
	}

	/// <summary>
	/// The per-change path records creation time exactly as the reconciler does; otherwise a file's
	/// recorded age would depend on which of the two discovered it.
	/// </summary>
	[Fact]
	public async Task A_New_File_Is_Recorded_With_Its_Own_Creation_Time_Not_The_Time_Of_The_Change()
	{
		using TempFolder folder = new();
		string path = folder.Combine("aged.md");
		await File.WriteAllTextAsync(path, "written long ago");
		TimestampFixture.TryBackdate(path);

		RecordingStore store = new();

		await Run(Pipeline(folder, store), Changed(folder, "aged.md", FileChangeKind.Created));

		DateTime recorded = store.Applied.Should().ContainSingle().Which.Current!.CreatedUtc;
		recorded.Should().Be(FileTimestamps.ReadCreatedUtc(new FileInfo(path)));
		TimestampFixture.ShouldBeTheBackdatedTimeWhereItTook(path, recorded);
	}

	[Fact]
	public async Task An_Edited_File_Keeps_The_Creation_Time_Already_Recorded()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("doc.md"), "after the edit");

		DateTime recordedCreation = new(2015, 6, 7, 8, 9, 10, DateTimeKind.Utc);
		RecordingStore store = new();
		store.Seed("doc.md", "a-hash-from-before-the-edit", recordedCreation);

		await Run(Pipeline(folder, store), Changed(folder, "doc.md", FileChangeKind.Modified));

		store.Applied.Should().ContainSingle()
			.Which.Current!.CreatedUtc.Should().Be(recordedCreation, "editing a file does not create it");
	}

	[Fact]
	public async Task A_Save_With_Identical_Content_Costs_No_Write()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("doc.md"), "unchanged");

		RecordingStore store = new();
		store.Seed("doc.md", await HashOf(folder.Combine("doc.md")));

		await Run(Pipeline(folder, store), Changed(folder, "doc.md", FileChangeKind.Modified));

		store.ApplyCalls.Should().Be(0);
	}

	[Fact]
	public async Task A_Deleted_File_The_Index_Knew_Is_Removed()
	{
		using TempFolder folder = new();

		RecordingStore store = new();
		store.Seed("gone.md", "some-hash");

		await Run(Pipeline(folder, store), Changed(folder, "gone.md", FileChangeKind.Deleted));

		store.Applied.Should().ContainSingle()
			.Which.Should().Match<ReconciledChange>(c => c.RelativePath == "gone.md" && c.Delta == FileDelta.Removed);
	}

	[Fact]
	public async Task A_Deleted_File_The_Index_Never_Knew_Costs_No_Write()
	{
		using TempFolder folder = new();

		RecordingStore store = new();

		await Run(Pipeline(folder, store), Changed(folder, "scratch.md", FileChangeKind.Deleted));

		store.ApplyCalls.Should().Be(0);
	}

	/// <summary>
	/// By the time a change is processed the file may already have been recreated. The disk is what is
	/// indexed, so a delete reported for a file that is plainly there must not remove it.
	/// </summary>
	[Fact]
	public async Task The_Event_Kind_Is_Not_Trusted_Over_The_Disk()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("back.md"), "recreated");

		RecordingStore store = new();

		await Run(Pipeline(folder, store), Changed(folder, "back.md", FileChangeKind.Deleted));

		store.Applied.Should().ContainSingle().Which.Delta.Should().Be(FileDelta.Added);
	}

	[Fact]
	public async Task A_Folder_Event_Costs_No_Write()
	{
		using TempFolder folder = new();
		Directory.CreateDirectory(folder.Combine("sub"));

		RecordingStore store = new();

		await Run(Pipeline(folder, store), Changed(folder, "sub", FileChangeKind.Created));

		store.ApplyCalls.Should().Be(0);
	}

	/// <summary>
	/// Both writers must key a file identically. If they did not, the second one to see the file would
	/// add it again under its own key, and the first record would never be removed.
	/// </summary>
	[Fact]
	public async Task A_Nested_File_Is_Keyed_Exactly_As_The_Reconciler_Keys_It()
	{
		using TempFolder folder = new();
		Directory.CreateDirectory(folder.Combine("docs", "deep"));
		await File.WriteAllTextAsync(folder.Combine("docs", "deep", "note.md"), "nested");

		RecordingStore store = new();
		await new Reconciler(folder.Path, ".folderassistant", store, new XxHash64ContentHasher()).ReconcileAsync();
		store.Applied.Clear();

		await Run(Pipeline(folder, store), Changed(folder, Path.Combine("docs", "deep", "note.md"), FileChangeKind.Modified));

		store.ApplyCalls.Should().Be(1, "only the reconciler's pass wrote; the pipeline found the same record");
		store.Records.Keys.Should().ContainSingle().Which.Should().Be("docs/deep/note.md");
	}

	/// <summary>
	/// The settle probe and the hash are separate opens, so a writer can take the file between them.
	/// That is a file which turned out not to be settled — not a failed change.
	/// </summary>
	[Fact]
	public async Task A_Hash_That_Loses_To_A_Writer_Asks_To_Be_Retried_And_Writes_Nothing()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("held.md"), "being written");

		RecordingStore store = new();
		ChangePipeline pipeline = Pipeline(folder, store, hasher: new FlakyHasher(failures: int.MaxValue));

		bool retry = await pipeline.ProcessOnceAsync(Changed(folder, "held.md", FileChangeKind.Modified), CancellationToken.None);

		retry.Should().BeTrue();
		store.ApplyCalls.Should().Be(0);
	}

	/// <summary>
	/// The source has completed before the retry is due, which is the shutdown-shaped case: finishing
	/// when the source finishes would discard exactly the changes that met a busy file.
	/// </summary>
	[Fact]
	public async Task A_Change_That_Met_A_Busy_File_Still_Lands_After_The_Source_Has_Completed()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("held.md"), "finally written");

		RecordingStore store = new();
		FlakyHasher hasher = new(failures: 2);

		await Run(Pipeline(folder, store, hasher: hasher), Changed(folder, "held.md", FileChangeKind.Created));

		hasher.Calls.Should().Be(3);
		store.Applied.Should().ContainSingle().Which.Delta.Should().Be(FileDelta.Added);
	}

	[Fact]
	public async Task A_File_Still_Being_Written_Is_Retried_Until_It_Settles()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("copying.md"), "arriving");

		RecordingStore store = new();
		ScriptedSettler settler = new(SettleResult.Busy, SettleResult.Busy, SettleResult.Settled);

		await Run(Pipeline(folder, store, settler: settler), Changed(folder, "copying.md", FileChangeKind.Created));

		settler.Calls.Should().Be(3);
		store.Applied.Should().ContainSingle().Which.Delta.Should().Be(FileDelta.Added);
	}

	/// <summary>
	/// An editor can hold a file for hours. The event path gives up after its limit and leaves the file
	/// to the periodic reconcile, which retries unreadable files on every pass anyway.
	/// </summary>
	[Fact]
	public async Task A_File_That_Never_Settles_Is_Given_Up_After_The_Attempt_Limit()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("held.md"), "held forever");

		RecordingStore store = new();
		ScriptedSettler settler = new(SettleResult.Busy);

		await Run(
			Pipeline(folder, store, settler: settler, maxAttempts: 3),
			Changed(folder, "held.md", FileChangeKind.Modified));

		settler.Calls.Should().Be(3);
		store.ApplyCalls.Should().Be(0);
	}

	[Fact]
	public async Task A_Fault_On_One_Change_Does_Not_Stop_The_Changes_Behind_It()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("first.md"), "one");
		await File.WriteAllTextAsync(folder.Combine("second.md"), "two");

		RecordingStore store = new() { FailApplyCalls = 1 };

		await Run(
			Pipeline(folder, store),
			Changed(folder, "first.md", FileChangeKind.Created),
			Changed(folder, "second.md", FileChangeKind.Created));

		store.Applied.Should().ContainSingle().Which.RelativePath.Should().Be("second.md");
	}

	[Fact]
	public async Task Cancelling_Ends_The_Run_While_The_Source_Is_Still_Open()
	{
		using TempFolder folder = new();

		Channel<ObservedChange> source = Channel.CreateUnbounded<ObservedChange>();
		using CancellationTokenSource stopping = new();

		Task run = Pipeline(folder, new RecordingStore()).RunAsync(source.Reader, stopping.Token);
		await Task.Delay(50);
		await stopping.CancelAsync();

		Func<Task> stop = async () => await run;

		await stop.Should().CompleteWithinAsync(TimeSpan.FromSeconds(20));
	}

	// ── what it survives has to be visible, or it looks like nothing happened ──

	/// <summary>
	/// The catch-all is right: one change failing must not end processing for every change behind it.
	/// But this is where a change goes to die — nothing written, nothing queued to try it again, stale
	/// until a reconcile happens to pass over the file. Silent, a pipeline dropping every change it is
	/// handed is indistinguishable from one that has been handed nothing.
	/// </summary>
	[Fact]
	public async Task A_Dropped_Change_Is_Recorded_Instead_Of_Vanishing()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("doomed.md"), "one");

		CapturingLogger<ChangePipeline> log = new();

		await Run(
			Pipeline(folder, new RecordingStore { FailApplyCalls = 1 }, logger: log),
			Changed(folder, "doomed.md", FileChangeKind.Created));

		log.At(LogLevel.Warning).Should().ContainSingle()
			.Which.Message.Should().Contain("doomed.md", "whoever wonders why that file is stale needs its name");
	}

	/// <summary>
	/// The one outcome on this path that leaves the index stale with nothing queued to correct it. The
	/// file is left to the reconciler, which is a slower promise than the rest of this class makes.
	/// </summary>
	[Fact]
	public async Task Giving_Up_On_A_File_That_Never_Settles_Is_Recorded()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("held.md"), "held forever");

		CapturingLogger<ChangePipeline> log = new();

		await Run(
			Pipeline(folder, new RecordingStore(), settler: new ScriptedSettler(SettleResult.Busy), maxAttempts: 3, logger: log),
			Changed(folder, "held.md", FileChangeKind.Modified));

		LoggedLine given = log.At(LogLevel.Warning).Should().ContainSingle().Subject;

		given.Message.Should().Contain("held.md").And.Contain("3");
	}

	/// <summary>
	/// A change that lost a race and then landed is not a fault, and reporting it as one would make an
	/// ordinary save look like a problem. Debug: it explains a delay, nothing more.
	/// </summary>
	[Fact]
	public async Task A_Hash_That_Lost_To_A_Writer_Is_Recorded_At_Debug_And_Nothing_Louder()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("busy.md"), "being written");

		CapturingLogger<ChangePipeline> log = new();

		await Run(
			Pipeline(folder, new RecordingStore(), hasher: new FlakyHasher(failures: 1), logger: log),
			Changed(folder, "busy.md", FileChangeKind.Created));

		log.At(LogLevel.Debug).Should().ContainSingle()
			.Which.Message.Should().Contain("busy.md");

		log.Lines.Should().OnlyContain(
			line => line.Level == LogLevel.Debug,
			"the change was retried and landed, which is the rule working rather than failing");
	}

	private static ChangePipeline Pipeline(
		TempFolder folder,
		IIndexStore store,
		IContentHasher? hasher = null,
		IFileSettler? settler = null,
		int maxAttempts = ChangePipeline.DefaultMaxAttempts,
		ILogger<ChangePipeline>? logger = null)
		=> new(
			folder.Path,
			store,
			hasher ?? new XxHash64ContentHasher(),
			settler ?? new FileSettler(TimeSpan.Zero),
			retryDelay: TimeSpan.FromMilliseconds(10),
			maxAttempts,
			logger);

	private static ObservedChange Changed(TempFolder folder, string relativePath, FileChangeKind kind)
		=> new(folder.Combine(relativePath), kind);

	/// <summary>
	/// Feeds the changes, completes the source, and requires the run to finish on its own. Asserting on
	/// completion rather than cancelling after a deadline is deliberate: a run that never finishes would
	/// otherwise stop quietly and leave the assertions to pass or fail on whatever it managed first.
	/// </summary>
	private static async Task Run(ChangePipeline pipeline, params ObservedChange[] changes)
	{
		Channel<ObservedChange> source = Channel.CreateUnbounded<ObservedChange>();

		foreach (ObservedChange change in changes)
		{
			await source.Writer.WriteAsync(change);
		}

		source.Writer.Complete();

		Func<Task> run = () => pipeline.RunAsync(source.Reader, CancellationToken.None);

		await run.Should().CompleteWithinAsync(TimeSpan.FromSeconds(20));
	}

	private static Task<string> HashOf(string path) => new XxHash64ContentHasher().HashAsync(path);

	/// <summary>A store that applies what it is given, so a second writer sees the first one's records.</summary>
	private sealed class RecordingStore : IIndexStore
	{
		public Dictionary<string, FileRecord> Records { get; } = new(StringComparer.Ordinal);

		public List<ReconciledChange> Applied { get; } = [];

		public int ApplyCalls { get; private set; }

		public int FailApplyCalls { get; init; }

		public void Seed(string relativePath, string contentHash, DateTime createdUtc = default)
			=> Records[relativePath] = new FileRecord(relativePath, contentHash, 0, createdUtc);

		public Task<IReadOnlyDictionary<string, FileRecord>> ReadAllAsync(CancellationToken cancellationToken = default)
			=> Task.FromResult<IReadOnlyDictionary<string, FileRecord>>(Records);

		public Task<FileRecord?> ReadAsync(string relativePath, CancellationToken cancellationToken = default)
			=> Task.FromResult(Records.GetValueOrDefault(relativePath));

		public Task ApplyAsync(IReadOnlyList<ReconciledChange> changes, CancellationToken cancellationToken = default)
		{
			ApplyCalls++;

			if (ApplyCalls <= FailApplyCalls)
			{
				return Task.FromException(new InvalidOperationException("the store is unavailable"));
			}

			foreach (ReconciledChange change in changes)
			{
				Applied.Add(change);

				if (change.Delta == FileDelta.Removed)
				{
					Records.Remove(change.RelativePath);
				}
				else
				{
					Records[change.RelativePath] = change.Current!;
				}
			}

			return Task.CompletedTask;
		}
	}

	/// <summary>
	/// Loses to a writer a set number of times, then reads normally. A real race between the probe and
	/// the hash cannot be staged on demand, and holding a real lock would assert the operating system's
	/// timing alongside the pipeline's rule.
	/// </summary>
	private sealed class FlakyHasher(int failures) : IContentHasher
	{
		private readonly XxHash64ContentHasher _inner = new();

		public int Calls { get; private set; }

		public Task<string> HashAsync(string absolutePath, CancellationToken cancellationToken = default)
		{
			Calls++;

			return Calls <= failures
				? throw new IOException("The process cannot access the file because it is being used by another process.")
				: _inner.HashAsync(absolutePath, cancellationToken);
		}
	}

	/// <summary>Answers from a script; the last answer repeats.</summary>
	private sealed class ScriptedSettler(params SettleResult[] script) : IFileSettler
	{
		public int Calls { get; private set; }

		public Task<SettleResult> SettleAsync(string absolutePath, CancellationToken cancellationToken)
		{
			SettleResult result = script[Math.Min(Calls, script.Length - 1)];
			Calls++;

			return Task.FromResult(result);
		}
	}
}
