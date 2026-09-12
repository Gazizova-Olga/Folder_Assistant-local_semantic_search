using System.Threading.Channels;
using FluentAssertions;
using FolderAssistant.Indexing.Watching;

namespace FolderAssistant.Tests.Indexing;

/// <summary>
/// A writer inside this process reporting what it changed, rather than waiting for the operating
/// system to report it back.
///
/// <para>
/// Most of these need no filesystem at all: the host is told a path changed and never touches it,
/// so what they assert is the routing and the settling, not the machine's timing. The one exception
/// is the test that has a real write and a report race each other for the same file, which is the
/// case the routing exists for and cannot be staged any other way.
/// </para>
/// </summary>
public sealed class WriteThroughNotificationTests
{
	private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(100);
	private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

	[Fact]
	public async Task A_Reported_Change_Settles_Like_An_Observed_One()
	{
		using TempFolder folder = new();
		await using FileSystemWatcherHost host = new(folder.Path, ".folderassistant", Window);

		host.Start();

		await host.NotifyChangedAsync(folder.Combine("notes.md"));

		ObservedChange change = await ReadOneAsync(host.Changes);

		Path.GetFileName(change.Path).Should().Be("notes.md");
		change.Kind.Should().Be(FileChangeKind.Modified);
	}

	[Fact]
	public async Task A_Reported_Delete_Settles_As_A_Delete()
	{
		using TempFolder folder = new();
		await using FileSystemWatcherHost host = new(folder.Path, ".folderassistant", Window);

		host.Start();

		await host.NotifyDeletedAsync(folder.Combine("gone.md"));

		ObservedChange change = await ReadOneAsync(host.Changes);

		change.Kind.Should().Be(FileChangeKind.Deleted);
	}

	/// <summary>
	/// The reason a report is routed through the debouncer instead of past it. A writer rewriting
	/// one file a dozen times — a batch of edits applied one at a time, a burst of separate calls —
	/// must cost one settle-hash-embed cycle, exactly as it does for an editor saving repeatedly.
	/// Reporting straight into the consumer would give that case a pass per write, which is worse
	/// than not reporting at all and letting the watcher find it.
	/// </summary>
	[Fact]
	public async Task Reports_Closer_Together_Than_The_Window_Cost_One_Change()
	{
		TimeSpan window = TimeSpan.FromSeconds(1);

		using TempFolder folder = new();
		await using FileSystemWatcherHost host = new(folder.Path, ".folderassistant", window);

		host.Start();

		string path = folder.Combine("list.md");

		for (int i = 0; i < 10; i++)
		{
			await host.NotifyChangedAsync(path);
		}

		await ReadOneAsync(host.Changes);

		// Longer than the window, so a second change would have settled and arrived by now if the
		// reports had each been taken on their own.
		await Task.Delay(window + window);

		host.Changes.TryRead(out ObservedChange? extra).Should()
			.BeFalse("reports closer together than the window are one edit", extra?.Path);
	}

	/// <summary>
	/// A write made in this process raises the operating system's events as well, so the report and
	/// the watcher describe the same edit. They have to land on one pending entry: two would mean
	/// reporting a change bought a duplicate pass rather than an earlier one.
	/// </summary>
	[Fact]
	public async Task A_Report_Merges_With_The_Watchers_Own_Events_For_That_File()
	{
		TimeSpan window = TimeSpan.FromSeconds(1);

		using TempFolder folder = new();
		await using FileSystemWatcherHost host = new(folder.Path, ".folderassistant", window);

		host.Start();
		await LetTheWatcherAttachAsync();

		string path = folder.Combine("notes.md");

		await File.WriteAllTextAsync(path, "alpha beta gamma");
		await host.NotifyChangedAsync(path);

		ObservedChange change = await ReadOneAsync(host.Changes);

		Path.GetFileName(change.Path).Should().Be("notes.md");

		await Task.Delay(window + window);

		host.Changes.TryRead(out ObservedChange? extra).Should()
			.BeFalse("the report and the watcher's events describe one edit", extra?.Path);
	}


	/// <summary>
	/// A path is a key here, so two spellings of one file are two files. The watcher reports a
	/// canonical full path and a caller reports whatever it happened to build, so without
	/// canonicalising, the merging above would fail quietly — two pending entries, two passes, and
	/// nothing to see in either.
	/// </summary>
	[Fact]
	public async Task Reports_Coalesce_Whatever_Spelling_The_Caller_Used()
	{
		TimeSpan window = TimeSpan.FromSeconds(1);

		using TempFolder folder = new();
		await using FileSystemWatcherHost host = new(folder.Path, ".folderassistant", window);

		host.Start();

		string canonical = folder.Combine("notes.md");
		string roundabout = Path.Combine(folder.Path, "sub", "..", "notes.md");

		await host.NotifyChangedAsync(canonical);
		await host.NotifyChangedAsync(roundabout);

		ObservedChange change = await ReadOneAsync(host.Changes);

		change.Path.Should().Be(canonical);

		await Task.Delay(window + window);

		host.Changes.TryRead(out ObservedChange? extra).Should()
			.BeFalse("one file reported twice is one change, however it was spelled", extra?.Path);
	}
	/// <summary>
	/// A report is not a way around the exclusions. The metadata folder above all: the index lives
	/// there, so a writer reporting its own bookkeeping would feed the loop the watcher refuses to.
	/// </summary>
	[Fact]
	public async Task Reports_For_Paths_That_Are_Never_Reported_Are_Refused()
	{
		using TempFolder folder = new();
		await using FileSystemWatcherHost host = new(folder.Path, ".folderassistant", Window);

		host.Start();

		await host.NotifyChangedAsync(folder.Combine(".folderassistant", "manifest.db"));
		await host.NotifyChangedAsync(folder.Combine("notes.md.tmp"));

		// The tracer, reported last: once it arrives, the two above have had at least as long and
		// did not. Without it this would only prove the test waited.
		await host.NotifyChangedAsync(folder.Combine("notes.md"));

		ObservedChange change = await ReadOneAsync(host.Changes);

		Path.GetFileName(change.Path).Should().Be("notes.md");

		// And nothing behind it: the two refusals must be refusals, not a reordering.
		host.Changes.TryRead(out ObservedChange? extra).Should()
			.BeFalse("a report does not reach paths the watcher itself would never report", extra?.Path);
	}

	/// <summary>
	/// Nothing drains the debouncer before the host starts, so a report taken then would sit in it
	/// unpublished — and would surface later as a change with no edit behind it. Dropping it loses
	/// nothing: the reconcile that follows a start compares the whole folder.
	/// </summary>
	[Fact]
	public async Task A_Report_Made_Before_The_Host_Starts_Is_Dropped()
	{
		using TempFolder folder = new();
		await using FileSystemWatcherHost host = new(folder.Path, ".folderassistant", Window);

		await host.NotifyChangedAsync(folder.Combine("early.md"));

		host.Start();

		await host.NotifyChangedAsync(folder.Combine("after.md"));

		ObservedChange change = await ReadOneAsync(host.Changes);

		Path.GetFileName(change.Path).Should().Be("after.md");

		host.Changes.TryRead(out ObservedChange? extra).Should()
			.BeFalse("a report made while nothing was running is dropped, not held", extra?.Path);
	}

	/// <summary>
	/// The caller's token governs the call, not the file operation behind it: a cancelled turn
	/// stops reporting rather than recording work nobody is waiting on.
	/// </summary>
	[Fact]
	public async Task A_Report_On_A_Cancelled_Token_Is_Not_Recorded()
	{
		using TempFolder folder = new();
		await using FileSystemWatcherHost host = new(folder.Path, ".folderassistant", Window);

		host.Start();

		using CancellationTokenSource cancelled = new();
		await cancelled.CancelAsync();

		Func<Task> report = async () =>
			await host.NotifyChangedAsync(folder.Combine("notes.md"), cancelled.Token);

		await report.Should().ThrowAsync<OperationCanceledException>();

		await Task.Delay(Window + Window);

		host.Changes.TryRead(out ObservedChange? extra).Should().BeFalse(extra?.Path);
	}

	/// <summary>
	/// FileSystemWatcher does not report anything until the OS has registered the subscription, and
	/// a file created in that gap is simply missed.
	/// </summary>
	private static Task LetTheWatcherAttachAsync() => Task.Delay(TimeSpan.FromMilliseconds(300));

	private static async Task<ObservedChange> ReadOneAsync(ChannelReader<ObservedChange> reader)
	{
		using CancellationTokenSource deadline = new(Deadline);

		return await reader.ReadAsync(deadline.Token);
	}
}
