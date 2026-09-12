using System.Threading.Channels;
using FluentAssertions;
using FolderAssistant.Indexing.Watching;

namespace FolderAssistant.Tests.Indexing;

/// <summary>
/// The host against a real <see cref="FileSystemWatcher"/> and a real folder.
///
/// <para>
/// The settling rule and the exclusion rule are asserted directly elsewhere, as functions. What is
/// left for this class is the one thing those cannot cover: that operating-system events actually
/// reach the debouncer and come out of the channel. It is the only place here that waits on the
/// filesystem, and the deadlines are deliberately generous — a slow machine should make this test
/// slow, not red.
/// </para>
/// </summary>
public sealed class FileSystemWatcherHostTests
{
	private static readonly IndexablePathFilter Filter = new(".folderassistant");

	private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(100);
	private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

	[Fact]
	public async Task A_New_File_Reaches_The_Channel_As_A_Settled_Change()
	{
		using TempFolder folder = new();
		await using FileSystemWatcherHost host = new(folder.Path, Filter, Window);

		host.Start();
		await LetTheWatcherAttachAsync();

		await File.WriteAllTextAsync(folder.Combine("notes.md"), "alpha beta gamma");

		ObservedChange change = await ReadOneAsync(host.Changes);

		Path.GetFileName(change.Path).Should().Be("notes.md");
		change.Kind.Should().Be(FileChangeKind.Created);
	}

	/// <summary>
	/// The reason the stage exists. Ten writes in quick succession are one edit as far as anything
	/// downstream is concerned, and hashing and embedding the file ten times for it is the cost being
	/// avoided.
	///
	/// <para>
	/// The window here is a second, far longer than the one the other tests use, and that is the
	/// point rather than an accident. The rule promises to collapse writes that are closer together
	/// than the window — not to collapse a burst of any duration. An earlier version of this test
	/// spaced ten writes 10ms apart against a 100ms window and passed repeatedly in isolation, then
	/// failed under a full-suite run, where thread-pool scheduling stretched one of those gaps past
	/// the window and the path legitimately settled mid-burst. The test was wrong, not the rule: it
	/// asserted a guarantee the debouncer does not make. Sized so that only a pause of a full second
	/// could break it, the burst is genuinely tighter than the window it is being collapsed by.
	/// </para>
	/// </summary>
	[Fact]
	public async Task Writes_Closer_Together_Than_The_Window_Produce_One_Change()
	{
		TimeSpan window = TimeSpan.FromSeconds(1);

		using TempFolder folder = new();
		await using FileSystemWatcherHost host = new(folder.Path, Filter, window);

		host.Start();
		await LetTheWatcherAttachAsync();

		string path = folder.Combine("notes.md");

		for (int i = 0; i < 10; i++)
		{
			await File.WriteAllTextAsync(path, $"revision {i}");
		}

		await ReadOneAsync(host.Changes);

		// Nothing further should arrive: the run collapsed into the change already read. A wait longer
		// than the window rather than an immediate check, so a second change would have settled and
		// shown up if the collapsing were broken.
		await Task.Delay(window + window);

		host.Changes.TryRead(out ObservedChange? extra).Should()
			.BeFalse("writes closer together than the window are one edit", extra?.Path);
	}

	/// <summary>
	/// The database lives inside the folder being watched, so its own writes arrive as events. Left
	/// unfiltered they would keep the folder permanently busy.
	/// </summary>
	[Fact]
	public async Task Writes_Inside_The_Metadata_Folder_Never_Surface()
	{
		using TempFolder folder = new();
		Directory.CreateDirectory(folder.Combine(".folderassistant"));

		await using FileSystemWatcherHost host = new(folder.Path, Filter, Window);

		host.Start();
		await LetTheWatcherAttachAsync();

		await File.WriteAllTextAsync(folder.Combine(".folderassistant", "manifest.db"), "not really a database");

		// A real file afterwards, as the tracer: once it arrives, the metadata write has had at least
		// as long to arrive and did not. Without it this would only prove the test waited.
		await File.WriteAllTextAsync(folder.Combine("notes.md"), "alpha");

		ObservedChange change = await ReadOneAsync(host.Changes);

		Path.GetFileName(change.Path).Should().Be("notes.md");
	}

	[Fact]
	public async Task Starting_Twice_Is_Refused()
	{
		using TempFolder folder = new();
		await using FileSystemWatcherHost host = new(folder.Path, Filter, Window);

		host.Start();

		Action restart = host.Start;

		restart.Should().Throw<InvalidOperationException>();
	}

	/// <summary>
	/// Disposal completes the channel, so a consumer looping over it terminates rather than hanging
	/// on a host that has stopped.
	/// </summary>
	[Fact]
	public async Task Disposing_Completes_The_Channel()
	{
		using TempFolder folder = new();
		FileSystemWatcherHost host = new(folder.Path, Filter, Window);

		host.Start();
		await host.DisposeAsync();

		Func<Task> drain = async () =>
		{
			await foreach (ObservedChange _ in host.Changes.ReadAllAsync())
			{
				// Draining to completion; the assertion is that this ends.
			}
		};

		await drain.Should().CompleteWithinAsync(Deadline);
	}

	/// <summary>
	/// FileSystemWatcher does not report anything until the OS has registered the subscription, and
	/// a file created in that gap is simply missed. Real deployments have a reconciler for exactly
	/// this; a test does not, so it waits.
	/// </summary>
	private static Task LetTheWatcherAttachAsync() => Task.Delay(TimeSpan.FromMilliseconds(300));

	private static async Task<ObservedChange> ReadOneAsync(ChannelReader<ObservedChange> reader)
	{
		using CancellationTokenSource deadline = new(Deadline);

		return await reader.ReadAsync(deadline.Token);
	}
}
