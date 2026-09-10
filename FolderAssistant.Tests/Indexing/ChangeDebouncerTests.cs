using FluentAssertions;
using FolderAssistant.Indexing.Watching;

namespace FolderAssistant.Tests.Indexing;

/// <summary>
/// The settling rule, driven by a supplied clock rather than by waiting. Every assertion here is
/// about what the rule says, not about whether the machine was fast enough to observe it.
/// </summary>
public sealed class ChangeDebouncerTests
{
	private static readonly DateTimeOffset Start = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
	private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(750);

	[Fact]
	public void A_Change_Is_Not_Published_Until_Its_Path_Has_Gone_Quiet()
	{
		ChangeDebouncer debouncer = new(Window);

		debouncer.Observe("a.md", FileChangeKind.Modified, Start);

		debouncer.DrainSettled(Start + TimeSpan.FromMilliseconds(749)).Should().BeEmpty();
		debouncer.DrainSettled(Start + Window).Should().ContainSingle()
			.Which.Path.Should().Be("a.md");
	}

	/// <summary>
	/// The point of the whole stage. An editor saving produces a run of events; each one restarts the
	/// window, and the file is reported once when the writing stops.
	/// </summary>
	[Fact]
	public void A_Burst_On_One_Path_Settles_Once_And_Each_Event_Restarts_The_Window()
	{
		ChangeDebouncer debouncer = new(Window);

		for (int i = 0; i < 10; i++)
		{
			debouncer.Observe("a.md", FileChangeKind.Modified, Start + TimeSpan.FromMilliseconds(i * 100));
		}

		// 900ms after the first event, but only 0ms after the last.
		debouncer.DrainSettled(Start + TimeSpan.FromMilliseconds(900)).Should().BeEmpty();

		debouncer.DrainSettled(Start + TimeSpan.FromMilliseconds(1650)).Should().ContainSingle();
	}

	[Fact]
	public void A_Path_Still_Being_Written_Does_Not_Hold_Back_One_That_Has_Settled()
	{
		ChangeDebouncer debouncer = new(Window);

		debouncer.Observe("quiet.md", FileChangeKind.Modified, Start);
		debouncer.Observe("busy.md", FileChangeKind.Modified, Start + TimeSpan.FromMilliseconds(700));

		IReadOnlyList<ObservedChange> settled = debouncer.DrainSettled(Start + TimeSpan.FromMilliseconds(800));

		settled.Should().ContainSingle().Which.Path.Should().Be("quiet.md");
		debouncer.HasPending.Should().BeTrue();
	}

	/// <summary>
	/// A scratch file written and cleaned up inside one window never existed downstream. Reporting
	/// the pair would cost an index and an un-index of a document that is already gone — and that is
	/// the shape of the temporary file an atomic save leaves beside its target.
	/// </summary>
	[Fact]
	public void A_File_Created_And_Deleted_Inside_One_Window_Is_Never_Reported()
	{
		ChangeDebouncer debouncer = new(Window);

		debouncer.Observe("scratch.md", FileChangeKind.Created, Start);
		debouncer.Observe("scratch.md", FileChangeKind.Deleted, Start + TimeSpan.FromMilliseconds(10));

		debouncer.HasPending.Should().BeFalse();
		debouncer.DrainSettled(Start + TimeSpan.FromSeconds(10)).Should().BeEmpty();
	}

	[Theory]
	// A new file written a few times is still, to a consumer holding no prior state, a new file.
	[InlineData(FileChangeKind.Created, FileChangeKind.Modified, FileChangeKind.Created)]
	// Deleted after being created in the same window: see the annihilation test above.
	[InlineData(FileChangeKind.Modified, FileChangeKind.Deleted, FileChangeKind.Deleted)]
	// Save-via-rename: the target is removed and put back. The consumer may already know the path,
	// so this is a modification rather than a create it could refuse as a duplicate.
	[InlineData(FileChangeKind.Deleted, FileChangeKind.Created, FileChangeKind.Modified)]
	[InlineData(FileChangeKind.Modified, FileChangeKind.Modified, FileChangeKind.Modified)]
	[InlineData(FileChangeKind.Created, FileChangeKind.Created, FileChangeKind.Created)]
	public void Two_Events_On_One_Path_Fold_To_Their_Net_Effect(
		FileChangeKind first, FileChangeKind second, FileChangeKind expected)
	{
		ChangeDebouncer debouncer = new(Window);

		debouncer.Observe("a.md", first, Start);
		debouncer.Observe("a.md", second, Start + TimeSpan.FromMilliseconds(10));

		debouncer.DrainSettled(Start + TimeSpan.FromSeconds(5))
			.Should().ContainSingle()
			.Which.Kind.Should().Be(expected);
	}

	[Fact]
	public void A_Settled_Change_Is_Published_Once_And_Then_Forgotten()
	{
		ChangeDebouncer debouncer = new(Window);

		debouncer.Observe("a.md", FileChangeKind.Modified, Start);

		debouncer.DrainSettled(Start + Window).Should().ContainSingle();
		debouncer.DrainSettled(Start + Window + Window).Should().BeEmpty();
		debouncer.HasPending.Should().BeFalse();
	}

	/// <summary>
	/// Shutdown must not swallow edits that are real and already made. Reporting them a moment early
	/// costs a hash of an unchanged file; dropping them leaves the index stale until something else
	/// happens to notice.
	/// </summary>
	[Fact]
	public void Draining_Everything_Releases_Changes_Still_Inside_Their_Window()
	{
		ChangeDebouncer debouncer = new(Window);

		debouncer.Observe("a.md", FileChangeKind.Modified, Start);
		debouncer.Observe("b.md", FileChangeKind.Created, Start);

		debouncer.DrainAll().Should().HaveCount(2);
		debouncer.HasPending.Should().BeFalse();
	}

	[Fact]
	public void A_Negative_Window_Is_Refused()
	{
		Action construct = () => _ = new ChangeDebouncer(TimeSpan.FromMilliseconds(-1));

		construct.Should().Throw<ArgumentOutOfRangeException>();
	}
}
