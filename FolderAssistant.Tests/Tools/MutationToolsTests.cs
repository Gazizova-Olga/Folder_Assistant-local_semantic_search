using System.Text;
using FluentAssertions;
using FolderAssistant.Indexing.Watching;
using FolderAssistant.Tools;

namespace FolderAssistant.Tests.Tools;

/// <summary>
/// The mutation tools (SPEC-101), each asserted on the bytes it leaves on disk and the report it makes —
/// never through an index that would still look right after a torn or misplaced write.
/// </summary>
public sealed class MutationToolsTests
{
	private const String Metadata = ".folderassistant";

	private static (MutationTools Tools, RecordingNotifier Notifier) Tools(TempFolder root)
	{
		RecordingNotifier notifier = new();

		return (new MutationTools(new WorkspacePathGuard(root.Path, Metadata), notifier), notifier);
	}

	// --- Create -------------------------------------------------------------------------------------

	[Fact]
	public async Task Create_Writes_The_File_Makes_Its_Parents_And_Reports_A_Creation()
	{
		using TempFolder root = new();
		(MutationTools tools, RecordingNotifier notifier) = Tools(root);

		FileCreated created = await tools.Create(Path.Combine("docs", "new.md"), "hello\nworld");

		created.Path.Should().Be(Path.Combine("docs", "new.md"));
		created.Bytes.Should().Be(11);
		created.Note.Should().BeNull();
		(await File.ReadAllBytesAsync(root.Combine("docs", "new.md"))).Should().Equal(Encoding.UTF8.GetBytes("hello\nworld"));
		notifier.Reports.Should().Equal((FileChangeKind.Created, root.Combine("docs", "new.md")));
		Directory.GetFiles(root.Combine("docs"), "*.tmp").Should().BeEmpty();
	}

	[Fact]
	public async Task Create_Refuses_An_Existing_File_A_Directory_And_A_Refused_Path()
	{
		using TempFolder root = new();
		await File.WriteAllTextAsync(root.Combine("exists.txt"), "old");
		Directory.CreateDirectory(root.Combine("dir"));
		(MutationTools tools, RecordingNotifier notifier) = Tools(root);

		Func<Task> existing = () => tools.Create("exists.txt", "new");
		Func<Task> directory = () => tools.Create("dir", "new");
		Func<Task> outside = () => tools.Create(Path.Combine("..", "escape.txt"), "new");
		Func<Task> metadata = () => tools.Create(Path.Combine(Metadata, "note.txt"), "new");

		await existing.Should().ThrowAsync<IOException>().WithMessage("*already exists*");
		await directory.Should().ThrowAsync<IOException>().WithMessage("*is a directory*");
		await outside.Should().ThrowAsync<WorkspaceContainmentException>();
		await metadata.Should().ThrowAsync<WorkspaceContainmentException>();
		(await File.ReadAllTextAsync(root.Combine("exists.txt"))).Should().Be("old");
		notifier.Reports.Should().BeEmpty();
	}

	// --- Update -------------------------------------------------------------------------------------

	[Fact]
	public async Task Update_Replaces_Every_Occurrence_And_Reports_A_Change()
	{
		using TempFolder root = new();
		await File.WriteAllTextAsync(root.Combine("a.txt"), "cat and cat\ndog");
		(MutationTools tools, RecordingNotifier notifier) = Tools(root);

		FileUpdated updated = await tools.Update("a.txt", "cat", "bird");

		updated.Path.Should().Be("a.txt");
		updated.Replacements.Should().Be(2);
		updated.Note.Should().BeNull();
		(await File.ReadAllTextAsync(root.Combine("a.txt"))).Should().Be("bird and bird\ndog");
		notifier.Reports.Should().Equal((FileChangeKind.Modified, root.Combine("a.txt")));
	}

	[Fact]
	public async Task Update_Keeps_The_Byte_Order_Mark_And_The_Line_Endings_It_Found()
	{
		using TempFolder root = new();
		Byte[] bom = [0xEF, 0xBB, 0xBF];
		await File.WriteAllBytesAsync(root.Combine("bom.txt"), [.. bom, .. Encoding.UTF8.GetBytes("one\r\ntwo\r\n")]);
		await File.WriteAllTextAsync(root.Combine("plain.txt"), "one\ntwo\n");
		(MutationTools tools, _) = Tools(root);

		await tools.Update("bom.txt", "two", "2");
		await tools.Update("plain.txt", "two", "2");

		(await File.ReadAllBytesAsync(root.Combine("bom.txt"))).Should().Equal([.. bom, .. Encoding.UTF8.GetBytes("one\r\n2\r\n")]);
		(await File.ReadAllBytesAsync(root.Combine("plain.txt"))).Should().Equal(Encoding.UTF8.GetBytes("one\n2\n"));
	}

	[Fact]
	public async Task Update_With_No_Occurrence_Throws_And_Changes_Nothing()
	{
		using TempFolder root = new();
		await File.WriteAllTextAsync(root.Combine("a.txt"), "unchanged");
		DateTime written = File.GetLastWriteTimeUtc(root.Combine("a.txt"));
		(MutationTools tools, RecordingNotifier notifier) = Tools(root);

		Func<Task> act = () => tools.Update("a.txt", "missing", "x");

		await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*does not occur*");
		(await File.ReadAllTextAsync(root.Combine("a.txt"))).Should().Be("unchanged");
		File.GetLastWriteTimeUtc(root.Combine("a.txt")).Should().Be(written);
		notifier.Reports.Should().BeEmpty();
	}

	[Fact]
	public async Task Update_Passes_Case_And_Whole_Word_To_The_Replacer()
	{
		using TempFolder root = new();
		await File.WriteAllTextAsync(root.Combine("words.txt"), "Cat concatenate cat");
		(MutationTools tools, _) = Tools(root);

		FileUpdated wholeWord = await tools.Update("words.txt", "cat", "dog", wholeWord: true);
		FileUpdated ignoreCase = await tools.Update("words.txt", "CAT", "bird", ignoreCase: true);

		wholeWord.Replacements.Should().Be(1);
		ignoreCase.Replacements.Should().Be(2);
		(await File.ReadAllTextAsync(root.Combine("words.txt"))).Should().Be("bird conbirdenate dog");
	}

	[Fact]
	public async Task Update_Refuses_A_Missing_File_A_Binary_File_And_An_Empty_Find()
	{
		using TempFolder root = new();
		await File.WriteAllBytesAsync(root.Combine("blob.bin"), [0x50, 0x00, 0x4B]);
		(MutationTools tools, _) = Tools(root);

		Func<Task> missing = () => tools.Update("nope.txt", "a", "b");
		Func<Task> binary = () => tools.Update("blob.bin", "a", "b");
		Func<Task> empty = () => tools.Update("blob.bin", "", "b");

		await missing.Should().ThrowAsync<FileNotFoundException>();
		await binary.Should().ThrowAsync<InvalidDataException>();
		await empty.Should().ThrowAsync<ArgumentException>();
	}

	// --- ReplaceLines -------------------------------------------------------------------------------

	[Fact]
	public async Task ReplaceLines_Replaces_The_Range_And_Keeps_What_Follows_On_Its_Own_Line()
	{
		using TempFolder root = new();
		await File.WriteAllTextAsync(root.Combine("f.txt"), "a\nb\nc\nd\n");
		(MutationTools tools, RecordingNotifier notifier) = Tools(root);

		LinesReplaced replaced = await tools.ReplaceLines("f.txt", 2, 3, "B\nC2\nC3");

		replaced.Should().Be(new LinesReplaced("f.txt", 2, 3, 5, null));
		(await File.ReadAllTextAsync(root.Combine("f.txt"))).Should().Be("a\nB\nC2\nC3\nd\n");
		notifier.Reports.Should().Equal((FileChangeKind.Modified, root.Combine("f.txt")));
	}

	[Fact]
	public async Task ReplaceLines_Uses_The_Files_Own_Line_Ending()
	{
		using TempFolder root = new();
		await File.WriteAllTextAsync(root.Combine("crlf.txt"), "a\r\nb\r\nc");
		(MutationTools tools, _) = Tools(root);

		await tools.ReplaceLines("crlf.txt", 2, 2, "B");

		(await File.ReadAllTextAsync(root.Combine("crlf.txt"))).Should().Be("a\r\nB\r\nc");
	}

	[Fact]
	public async Task ReplaceLines_On_The_Last_Line_Without_A_Terminator_Adds_None()
	{
		using TempFolder root = new();
		await File.WriteAllTextAsync(root.Combine("f.txt"), "a\nb\nc");
		(MutationTools tools, _) = Tools(root);

		await tools.ReplaceLines("f.txt", 3, 3, "C");

		(await File.ReadAllTextAsync(root.Combine("f.txt"))).Should().Be("a\nb\nC");
	}

	[Fact]
	public async Task ReplaceLines_With_Empty_Text_Removes_The_Lines()
	{
		using TempFolder root = new();
		await File.WriteAllTextAsync(root.Combine("f.txt"), "a\nb\nc\nd");
		(MutationTools tools, _) = Tools(root);

		LinesReplaced replaced = await tools.ReplaceLines("f.txt", 2, 3, "");

		replaced.LinesRemoved.Should().Be(2);
		replaced.LinesInserted.Should().Be(0);
		replaced.TotalLines.Should().Be(2);
		(await File.ReadAllTextAsync(root.Combine("f.txt"))).Should().Be("a\nd");
	}

	[Fact]
	public async Task ReplaceLines_Replaces_A_Single_Line_File_Whole()
	{
		using TempFolder root = new();
		await File.WriteAllTextAsync(root.Combine("one.txt"), "only");
		(MutationTools tools, _) = Tools(root);

		await tools.ReplaceLines("one.txt", 1, 1, "new");

		(await File.ReadAllTextAsync(root.Combine("one.txt"))).Should().Be("new");
	}

	[Fact]
	public async Task ReplaceLines_Past_The_End_Or_With_A_Bad_Range_Throws_And_Changes_Nothing()
	{
		using TempFolder root = new();
		await File.WriteAllTextAsync(root.Combine("f.txt"), "a\nb");
		await File.WriteAllTextAsync(root.Combine("empty.txt"), "");
		(MutationTools tools, RecordingNotifier notifier) = Tools(root);

		Func<Task> past = () => tools.ReplaceLines("f.txt", 2, 3, "x");
		Func<Task> emptyFile = () => tools.ReplaceLines("empty.txt", 1, 1, "x");
		Func<Task> zero = () => tools.ReplaceLines("f.txt", 0, 1, "x");
		Func<Task> inverted = () => tools.ReplaceLines("f.txt", 2, 1, "x");

		await past.Should().ThrowAsync<ArgumentOutOfRangeException>();
		await emptyFile.Should().ThrowAsync<ArgumentOutOfRangeException>();
		await zero.Should().ThrowAsync<ArgumentOutOfRangeException>();
		await inverted.Should().ThrowAsync<ArgumentOutOfRangeException>();
		(await File.ReadAllTextAsync(root.Combine("f.txt"))).Should().Be("a\nb");
		notifier.Reports.Should().BeEmpty();
	}

	// --- Delete -------------------------------------------------------------------------------------

	[Fact]
	public async Task Delete_Removes_A_File_And_Reports_It()
	{
		using TempFolder root = new();
		await File.WriteAllTextAsync(root.Combine("gone.txt"), "x");
		(MutationTools tools, RecordingNotifier notifier) = Tools(root);

		FileDeleted deleted = await tools.Delete("gone.txt");

		deleted.Should().Be(new FileDeleted("gone.txt", 1, null));
		File.Exists(root.Combine("gone.txt")).Should().BeFalse();
		notifier.Reports.Should().Equal((FileChangeKind.Deleted, root.Combine("gone.txt")));
	}

	[Fact]
	public async Task Delete_Removes_A_Directory_And_Reports_Each_File_In_It()
	{
		using TempFolder root = new();
		Directory.CreateDirectory(root.Combine("dir", "deep"));
		await File.WriteAllTextAsync(root.Combine("dir", "a.txt"), "a");
		await File.WriteAllTextAsync(root.Combine("dir", "deep", "b.txt"), "b");
		await File.WriteAllTextAsync(root.Combine("keep.txt"), "keep");
		(MutationTools tools, RecordingNotifier notifier) = Tools(root);

		FileDeleted deleted = await tools.Delete("dir");

		deleted.FilesDeleted.Should().Be(2);
		Directory.Exists(root.Combine("dir")).Should().BeFalse();
		File.Exists(root.Combine("keep.txt")).Should().BeTrue();
		notifier.Reports.Should().BeEquivalentTo(new[]
		{
			(FileChangeKind.Deleted, root.Combine("dir", "a.txt")),
			(FileChangeKind.Deleted, root.Combine("dir", "deep", "b.txt")),
		});
	}

	[Fact]
	public async Task Delete_Refuses_The_Root_A_Missing_Path_And_A_Directory_Holding_A_Link()
	{
		using TempFolder root = new();
		using TempFolder outside = new();
		await File.WriteAllTextAsync(outside.Combine("secret.txt"), "secret");
		Directory.CreateDirectory(root.Combine("linked"));
		await File.WriteAllTextAsync(root.Combine("linked", "own.txt"), "own");
		LinkFixtures.CreateDirectoryLink(root.Combine("linked", "escape"), outside.Path);
		(MutationTools tools, RecordingNotifier notifier) = Tools(root);

		Func<Task> rootItself = () => tools.Delete(".");
		Func<Task> missing = () => tools.Delete("nope");
		Func<Task> linked = () => tools.Delete("linked");

		await rootItself.Should().ThrowAsync<InvalidOperationException>().WithMessage("*root*");
		await missing.Should().ThrowAsync<FileNotFoundException>();
		await linked.Should().ThrowAsync<InvalidOperationException>().WithMessage("*link*");
		File.Exists(root.Combine("linked", "own.txt")).Should().BeTrue();
		File.Exists(outside.Combine("secret.txt")).Should().BeTrue();
		notifier.Reports.Should().BeEmpty();
	}

	// --- The write path -----------------------------------------------------------------------------

	/// <summary>
	/// The reason the rename retries: a reader holding the file share-read — the indexer, delivering it —
	/// denies the replace on Windows, and the write has to wait it out rather than fail. Elsewhere a rename
	/// over an open file succeeds at once, and the test asserts the same outcome.
	/// </summary>
	[Fact]
	public async Task A_Write_Waits_Out_A_Reader_Holding_The_File()
	{
		using TempFolder root = new();
		await File.WriteAllTextAsync(root.Combine("held.txt"), "before");
		(MutationTools tools, _) = Tools(root);

		FileUpdated updated;
		using (FileStream reader = new(root.Combine("held.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
		{
			Task<FileUpdated> pending = tools.Update("held.txt", "before", "after");
			await Task.Delay(MutationTools.RetryFirstDelay * 6);
			await reader.DisposeAsync();
			updated = await pending;
		}

		updated.Replacements.Should().Be(1);
		(await File.ReadAllTextAsync(root.Combine("held.txt"))).Should().Be("after");
		Directory.GetFiles(root.Path, "*.tmp").Should().BeEmpty();
	}

	[Fact]
	public async Task A_Delete_Waits_Out_A_Reader_Holding_The_File()
	{
		using TempFolder root = new();
		await File.WriteAllTextAsync(root.Combine("held.txt"), "x");
		(MutationTools tools, _) = Tools(root);

		using (FileStream reader = new(root.Combine("held.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
		{
			Task<FileDeleted> pending = tools.Delete("held.txt");
			await Task.Delay(MutationTools.RetryFirstDelay * 6);
			await reader.DisposeAsync();
			await pending;
		}

		File.Exists(root.Combine("held.txt")).Should().BeFalse();
	}

	[Fact]
	public async Task A_Report_That_Fails_Is_A_Note_Not_A_Failure()
	{
		using TempFolder root = new();
		await File.WriteAllTextAsync(root.Combine("a.txt"), "old");
		(MutationTools tools, RecordingNotifier notifier) = Tools(root);
		notifier.Fails = true;

		FileCreated created = await tools.Create("b.txt", "new");
		FileUpdated updated = await tools.Update("a.txt", "old", "new");
		FileDeleted deleted = await tools.Delete("a.txt");

		created.Note.Should().Contain("not reported");
		updated.Note.Should().Contain("not reported");
		deleted.Note.Should().Contain("not reported");
		(await File.ReadAllTextAsync(root.Combine("b.txt"))).Should().Be("new");
		File.Exists(root.Combine("a.txt")).Should().BeFalse();
	}

	[Fact]
	public async Task A_Write_That_Fails_Leaves_No_Temporary_File_Behind()
	{
		using TempFolder root = new();
		await File.WriteAllTextAsync(root.Combine("a.txt"), "old");
		(MutationTools tools, _) = Tools(root);
		using CancellationTokenSource cancelled = new();
		await cancelled.CancelAsync();

		Func<Task> act = () => tools.Update("a.txt", "old", "new", cancellationToken: cancelled.Token);

		await act.Should().ThrowAsync<OperationCanceledException>();
		(await File.ReadAllTextAsync(root.Combine("a.txt"))).Should().Be("old");
		Directory.GetFiles(root.Path, "*.tmp").Should().BeEmpty();
	}
}
