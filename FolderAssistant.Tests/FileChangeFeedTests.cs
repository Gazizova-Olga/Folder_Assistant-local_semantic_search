using FluentAssertions;
using FolderAssistant.Indexing;

namespace FolderAssistant.Tests;

/// <summary>
/// The feed's job is to say "look again" without lying about when. These cover the three things it
/// has to get right: it notices an edit, it collapses a burst into one pass, and it does not report
/// the writes that indexing itself makes.
/// </summary>
public sealed class FileChangeFeedTests
{
	private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(80);
	private static readonly TimeSpan NoReconciliation = TimeSpan.Zero;

	[Fact]
	public async Task An_Edited_File_Produces_A_Signal()
	{
		using TempFolder folder = new();
		using FileSystemWatcherChangeFeed feed = new(folder.Path, ".folderassistant", Debounce, NoReconciliation);

		feed.Start();
		await File.WriteAllTextAsync(folder.Combine("notes.md"), "something");

		FolderChangeSignal? signal = await NextSignal(feed);

		signal.Should().NotBeNull();
	}

	/// <summary>
	/// The guard against the feed feeding itself. The folder database lives inside the analyzed
	/// folder, so every pass writes files this watcher can see; without the filter each pass would
	/// trigger the next one and the folder would index for as long as the process ran.
	///
	/// <para>Removing the filter fails this test.</para>
	/// </summary>
	[Fact]
	public async Task Writes_Inside_The_Metadata_Folder_Produce_Nothing()
	{
		using TempFolder folder = new();
		Directory.CreateDirectory(folder.Combine(".folderassistant"));

		using FileSystemWatcherChangeFeed feed = new(folder.Path, ".folderassistant", Debounce, NoReconciliation);

		feed.Start();
		await File.WriteAllTextAsync(folder.Combine(".folderassistant", "manifest.db"), "index write");

		FolderChangeSignal? signal = await NextSignal(feed);

		signal.Should().BeNull();
	}

	/// <summary>
	/// A configured metadata folder is honoured, not just the default name. The watcher is told the
	/// name that the rest of the application was configured with; hardcoding one here would make the
	/// self-triggering loop reappear for anyone who changed it.
	/// </summary>
	[Fact]
	public async Task A_Non_Default_Metadata_Folder_Name_Is_Honoured()
	{
		using TempFolder folder = new();
		Directory.CreateDirectory(folder.Combine("_index"));

		using FileSystemWatcherChangeFeed feed = new(folder.Path, "_index", Debounce, NoReconciliation);

		feed.Start();
		await File.WriteAllTextAsync(folder.Combine("_index", "manifest.db"), "index write");

		FolderChangeSignal? signal = await NextSignal(feed);

		signal.Should().BeNull();
	}

	[Fact]
	public async Task Build_Output_Is_Ignored()
	{
		using TempFolder folder = new();
		Directory.CreateDirectory(folder.Combine("obj"));

		using FileSystemWatcherChangeFeed feed = new(folder.Path, ".folderassistant", Debounce, NoReconciliation);

		feed.Start();
		await File.WriteAllTextAsync(folder.Combine("obj", "generated.cs"), "// build output");

		FolderChangeSignal? signal = await NextSignal(feed);

		signal.Should().BeNull();
	}

	/// <summary>
	/// A burst collapses into one signal. A consumer of this feed rescans the whole folder, so ten
	/// signals for ten edits buys ten identical passes; the channel holds one pending signal and the
	/// debounce restarts on every event.
	/// </summary>
	[Fact]
	public async Task A_Burst_Of_Edits_Collapses_Into_One_Signal()
	{
		using TempFolder folder = new();
		using FileSystemWatcherChangeFeed feed = new(folder.Path, ".folderassistant", Debounce, NoReconciliation);

		feed.Start();

		for (Int32 i = 0; i < 10; i++)
		{
			await File.WriteAllTextAsync(folder.Combine($"file-{i}.md"), "content");
		}

		FolderChangeSignal? first = await NextSignal(feed);
		FolderChangeSignal? second = await NextSignal(feed);

		first.Should().NotBeNull();
		second.Should().BeNull();
	}

	/// <summary>
	/// The signal names a reason but no file. A consumer cannot act on per-file detail — it rescans
	/// and diffs by content hash regardless — and a feed that promised that detail would be promising
	/// completeness it cannot deliver.
	/// </summary>
	[Fact]
	public async Task The_Signal_Carries_A_Reason_And_No_Per_File_Detail()
	{
		using TempFolder folder = new();
		using FileSystemWatcherChangeFeed feed = new(folder.Path, ".folderassistant", Debounce, NoReconciliation);

		feed.Start();
		await File.WriteAllTextAsync(folder.Combine("notes.md"), "something");

		FolderChangeSignal signal = (await NextSignal(feed))!;

		signal.Reason.Should().NotBeNullOrWhiteSpace();
		typeof(FolderChangeSignal).GetProperties().Should().ContainSingle();
	}

	/// <summary>The periodic rescan is the safety net for whatever the watcher never delivered.</summary>
	[Fact]
	public async Task The_Reconciliation_Tick_Signals_Without_Any_File_Event()
	{
		using TempFolder folder = new();
		using FileSystemWatcherChangeFeed feed = new(
			folder.Path, ".folderassistant", Debounce, TimeSpan.FromMilliseconds(100));

		feed.Start();

		FolderChangeSignal? signal = await NextSignal(feed, TimeSpan.FromSeconds(5));

		signal.Should().NotBeNull();
		signal!.Reason.Should().Be("periodic reconciliation");
	}

	/// <summary>Reads the next signal, or returns null if none arrives before the wait expires.</summary>
	private static async Task<FolderChangeSignal?> NextSignal(IFileChangeFeed feed, TimeSpan? within = null)
	{
		using CancellationTokenSource cancellation = new(within ?? TimeSpan.FromSeconds(2));

		IAsyncEnumerator<FolderChangeSignal> signals = feed.ReadAllAsync(cancellation.Token).GetAsyncEnumerator();

		try
		{
			return await signals.MoveNextAsync() ? signals.Current : null;
		}
		catch (OperationCanceledException)
		{
			// Nothing arrived. For a folder that should produce no signal, that is the assertion.
			return null;
		}
		finally
		{
			await signals.DisposeAsync();
		}
	}
}
