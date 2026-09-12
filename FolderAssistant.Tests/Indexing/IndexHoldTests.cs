using System.Threading.Channels;
using FluentAssertions;
using FolderAssistant.Indexing.Watching;

namespace FolderAssistant.Tests.Indexing;

/// <summary>
/// Holding everything back for as long as a caller says its work is still going on.
///
/// <para>
/// Each test asserts a negative first — that nothing was published while the hold was open — and
/// waits several poll intervals to do it, because a publication that is merely late looks exactly
/// like one that was suppressed if the wait is short enough. The positive half follows: whatever was
/// held arrives once the hold goes.
/// </para>
///
/// <para>
/// One property is deliberately <em>not</em> demonstrated. Releasing the last hold publishes
/// immediately rather than waiting out the poll interval, and no test here separates that from the
/// poll arriving a moment later — the two differ only in timing, and telling them apart would mean
/// racing the interval and calling the result a rule. What is asserted is that the release is what
/// lets the batch out, not how many milliseconds it took.
/// </para>
/// </summary>
public sealed class IndexHoldTests
{
	private static readonly IndexablePathFilter Filter = new(".folderassistant");

	private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(100);
	private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

	/// <summary>Several poll intervals, so "nothing arrived" is not "nothing arrived yet".</summary>
	private static readonly TimeSpan LongerThanAnyTick = TimeSpan.FromMilliseconds(600);

	[Fact]
	public async Task A_Hold_Keeps_A_Settled_Change_Back_Until_It_Is_Released()
	{
		using TempFolder folder = new();
		await using FileSystemWatcherHost host = new(folder.Path, Filter, Window);

		host.Start();

		IDisposable hold = host.BeginBatch();

		await host.NotifyChangedAsync(folder.Combine("notes.md"));
		await Task.Delay(LongerThanAnyTick);

		host.Changes.TryRead(out ObservedChange? early).Should()
			.BeFalse("the caller has not said its work is finished", early?.Path);

		hold.Dispose();

		ObservedChange change = await ReadOneAsync(host.Changes);

		Path.GetFileName(change.Path).Should().Be("notes.md");
	}

	/// <summary>
	/// Holds nest because the boundaries they mark do: an outer one for a whole piece of work, an
	/// inner one for a step inside it. Releasing the inner must not publish what the outer is still
	/// holding.
	/// </summary>
	[Fact]
	public async Task Holds_Nest_And_The_Batch_Goes_On_The_Last_Release()
	{
		using TempFolder folder = new();
		await using FileSystemWatcherHost host = new(folder.Path, Filter, Window);

		host.Start();

		IDisposable outer = host.BeginBatch();
		IDisposable inner = host.BeginBatch();

		await host.NotifyChangedAsync(folder.Combine("notes.md"));

		inner.Dispose();
		await Task.Delay(LongerThanAnyTick);

		host.Changes.TryRead(out ObservedChange? early).Should()
			.BeFalse("the outer hold is still open", early?.Path);

		outer.Dispose();

		ObservedChange change = await ReadOneAsync(host.Changes);

		Path.GetFileName(change.Path).Should().Be("notes.md");
	}

	/// <summary>
	/// A handle that is never disposed must cost a bounded delay, not an index that stops converging
	/// for the life of the process. The expiry is what makes a leak survivable, so it is asserted by
	/// leaking one.
	/// </summary>
	[Fact]
	public async Task A_Hold_That_Is_Never_Released_Expires()
	{
		TimeSpan maxHold = TimeSpan.FromMilliseconds(400);

		using TempFolder folder = new();
		await using FileSystemWatcherHost host = new(folder.Path, Filter, Window, maxHold);

		host.Start();

		_ = host.BeginBatch();

		await host.NotifyChangedAsync(folder.Combine("notes.md"));

		ObservedChange change = await ReadOneAsync(host.Changes);

		Path.GetFileName(change.Path).Should().Be("notes.md");
	}

	/// <summary>
	/// Disposing one handle twice must not release a hold the caller does not own. Undetectable
	/// without a second hold open behind it, which is why there is one here.
	/// </summary>
	[Fact]
	public async Task Disposing_A_Hold_Twice_Does_Not_Release_Another()
	{
		using TempFolder folder = new();
		await using FileSystemWatcherHost host = new(folder.Path, Filter, Window);

		host.Start();

		IDisposable first = host.BeginBatch();
		IDisposable second = host.BeginBatch();

		await host.NotifyChangedAsync(folder.Combine("notes.md"));

		first.Dispose();
		first.Dispose();

		await Task.Delay(LongerThanAnyTick);

		host.Changes.TryRead(out ObservedChange? early).Should()
			.BeFalse("the second hold is still open and was never released", early?.Path);

		second.Dispose();

		ObservedChange change = await ReadOneAsync(host.Changes);

		Path.GetFileName(change.Path).Should().Be("notes.md");
	}

	/// <summary>
	/// A hold suppresses everything pending, not only what was reported to it. The writes a caller
	/// makes raise the operating system's events as well, so a hold that covered only reports would
	/// suppress one copy of an edit and publish the other.
	/// </summary>
	[Fact]
	public async Task A_Hold_Covers_The_Watchers_Own_Events_Too()
	{
		using TempFolder folder = new();
		await using FileSystemWatcherHost host = new(folder.Path, Filter, Window);

		host.Start();
		await LetTheWatcherAttachAsync();

		IDisposable hold = host.BeginBatch();

		await File.WriteAllTextAsync(folder.Combine("notes.md"), "alpha beta gamma");
		await Task.Delay(LongerThanAnyTick);

		host.Changes.TryRead(out ObservedChange? early).Should()
			.BeFalse("a hold is not only about what was reported to it", early?.Path);

		hold.Dispose();

		ObservedChange change = await ReadOneAsync(host.Changes);

		Path.GetFileName(change.Path).Should().Be("notes.md");
	}


	/// <summary>
	/// The expiry runs from the first hold, not the most recent one. If nesting pushed it out, a
	/// caller that opens one hold per step would have exactly the unbounded hold the expiry exists
	/// to rule out — so the cap has to be on the whole held period, not on its last segment.
	///
	/// <para>
	/// The margins here are wide on purpose: the nested hold opens three quarters of the way through
	/// the cap, and the read is given as long again as the expiry has left. A machine slow enough to
	/// fail this would have to be slow by a second, not by a scheduling quantum.
	/// </para>
	/// </summary>
	[Fact]
	public async Task Nesting_Does_Not_Push_The_Expiry_Out()
	{
		TimeSpan maxHold = TimeSpan.FromSeconds(2);

		using TempFolder folder = new();
		await using FileSystemWatcherHost host = new(folder.Path, Filter, Window, maxHold);

		host.Start();

		_ = host.BeginBatch();
		await Task.Delay(TimeSpan.FromMilliseconds(1500));

		_ = host.BeginBatch();
		await host.NotifyChangedAsync(folder.Combine("notes.md"));

		Func<Task> read = async () =>
		{
			using CancellationTokenSource within = new(TimeSpan.FromMilliseconds(1500));
			await host.Changes.ReadAsync(within.Token);
		};

		await read.Should().NotThrowAsync("a hold opened inside another cannot extend the cap");
	}

	private static Task LetTheWatcherAttachAsync() => Task.Delay(TimeSpan.FromMilliseconds(300));

	private static async Task<ObservedChange> ReadOneAsync(ChannelReader<ObservedChange> reader)
	{
		using CancellationTokenSource deadline = new(Deadline);

		return await reader.ReadAsync(deadline.Token);
	}
}
