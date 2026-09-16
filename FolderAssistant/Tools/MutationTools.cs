using System.ComponentModel;
using System.Text;
using FolderAssistant.Indexing.Watching;

namespace FolderAssistant.Tools;

/// <summary>A file the tool created; <paramref name="Bytes"/> is what was written.</summary>
internal sealed record FileCreated(String Path, Int64 Bytes, String? Note);

/// <summary>A file the tool rewrote; <paramref name="Replacements"/> is how many occurrences were replaced.</summary>
internal sealed record FileUpdated(String Path, Int32 Replacements, String? Note);

/// <summary>A line range the tool replaced, and the file's line count afterwards.</summary>
internal sealed record LinesReplaced(String Path, Int32 LinesRemoved, Int32 LinesInserted, Int32 TotalLines, String? Note);

/// <summary>What the tool deleted: one file, or every file under a directory.</summary>
internal sealed record FileDeleted(String Path, Int32 FilesDeleted, String? Note);

/// <summary>
/// The mutation tools (SPEC-101): create a file, replace text in one, replace a line range, delete a
/// file or a directory. A second holder over a second guard, not four more methods on the read holder,
/// because the split is the contract that lets a roster grant reading without writing.
///
/// <para>
/// Every write goes through a temporary file beside the target and a rename over it, never a write in
/// place. A write in place holds a handle the indexer's share-read opens collide with, and a reader
/// that opened the file half-way through would read a torn document; a rename is one step, and the
/// file is always either the old whole or the new whole. The rename is retried, because a file the
/// indexer is reading denies it — reported as <see cref="IOException"/> or
/// <see cref="UnauthorizedAccessException"/> depending on the platform, so both are retried — and so
/// is a delete, since a share-read open carries no delete permission. The reads here do not retry, as
/// the read holder's do not: a read that fails has met a genuine external writer.
/// </para>
///
/// <para>
/// A completed mutation is reported to the indexing front end with its own kind — created, changed or
/// deleted, one report per file — so the index follows an edit this process made without waiting to
/// rediscover it. The report is advisory: it changes when the index catches up, never whether, and a
/// report that fails is said in the result's note rather than turning a successful write into a
/// reported failure.
/// </para>
/// </summary>
internal sealed class MutationTools
{
	internal const Int32 RetryAttempts = 6;
	internal static readonly TimeSpan RetryFirstDelay = TimeSpan.FromMilliseconds(25);

	private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
	private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);
	private static readonly Byte[] Bom = [0xEF, 0xBB, 0xBF];

	private readonly WorkspacePathGuard _guard;
	private readonly IIndexChangeNotifier _notifier;

	/// <param name="guard">The holder's own containment guard.</param>
	/// <param name="notifier">The running front end, told of every completed mutation.</param>
	public MutationTools(WorkspacePathGuard guard, IIndexChangeNotifier notifier)
	{
		ArgumentNullException.ThrowIfNull(guard);
		ArgumentNullException.ThrowIfNull(notifier);
		this._guard = guard;
		this._notifier = notifier;
	}

	[Description("Creates a new text file in the workspace with the given content, creating missing parent directories. Fails if the file already exists; use Update or ReplaceLines to change an existing file.")]
	public async Task<FileCreated> Create(
		[Description("A path relative to the workspace root.")] String path,
		[Description("The whole content of the new file.")] String content,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(content);

		GuardedPath resolved = this._guard.Resolve(path);
		String shown = Shown(resolved);
		if (Directory.Exists(resolved.FullPath))
		{
			throw new IOException($"'{shown}' is a directory.");
		}

		if (File.Exists(resolved.FullPath))
		{
			throw new IOException($"'{shown}' already exists; use Update or ReplaceLines to change it.");
		}

		Directory.CreateDirectory(Path.GetDirectoryName(resolved.FullPath)!);
		Byte[] bytes = Utf8WithoutBom.GetBytes(content);
		await WriteAtomically(resolved.FullPath, bytes, cancellationToken).ConfigureAwait(false);

		String? report = await Report(this._notifier.NotifyCreatedAsync, resolved.FullPath, cancellationToken).ConfigureAwait(false);

		return new FileCreated(shown, bytes.Length, Join(resolved.Note, report));
	}

	[Description("Replaces every occurrence of a literal string in a text file in the workspace. Fails if the string does not occur, so nothing is changed silently.")]
	public async Task<FileUpdated> Update(
		[Description("A path relative to the workspace root.")] String path,
		[Description("The exact text to find. Not a regular expression.")] String find,
		[Description("The text to put in its place; empty removes the occurrences.")] String replace,
		[Description("Match without regard to case.")] Boolean ignoreCase = false,
		[Description("Replace only where the text is not joined to a letter, digit or underscore on either side.")] Boolean wholeWord = false,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(find);
		ArgumentNullException.ThrowIfNull(replace);

		GuardedPath resolved = this._guard.Resolve(path);
		String shown = Shown(resolved);
		(String text, Boolean hadBom) = ReadText(resolved, shown);

		String rewritten = TextReplacer.Replace(text, find, replace, ignoreCase, wholeWord, out Int32 count);
		if (count == 0)
		{
			throw new InvalidOperationException($"'{find}' does not occur in '{shown}'; nothing was changed.");
		}

		await WriteAtomically(resolved.FullPath, Encode(rewritten, hadBom), cancellationToken).ConfigureAwait(false);

		String? report = await Report(this._notifier.NotifyChangedAsync, resolved.FullPath, cancellationToken).ConfigureAwait(false);

		return new FileUpdated(shown, count, Join(resolved.Note, report));
	}

	[Description("Replaces an inclusive range of lines in a text file in the workspace with new text, verbatim. Lines are 1-based, as ReadFile numbers them. Empty text removes the lines.")]
	public async Task<LinesReplaced> ReplaceLines(
		[Description("A path relative to the workspace root.")] String path,
		[Description("The first line to replace, 1-based.")] Int32 startLine,
		[Description("The last line to replace, inclusive.")] Int32 endLine,
		[Description("The text that takes the range's place; may hold several lines, or none.")] String text,
		CancellationToken cancellationToken = default)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(startLine, 1);
		ArgumentOutOfRangeException.ThrowIfLessThan(endLine, startLine);
		ArgumentNullException.ThrowIfNull(text);

		GuardedPath resolved = this._guard.Resolve(path);
		String shown = Shown(resolved);
		(String original, Boolean hadBom) = ReadText(resolved, shown);

		LineSpan span = LineRangeLocator.Locate(original, startLine, endLine);

		// The range's own terminator is kept, so what follows stays on its own line — unless the new text
		// is empty, in which case the lines are gone, terminator included, or it already ends in one.
		String inserted = text;
		if (span.EndsWithTerminator && inserted.Length > 0 && !inserted.EndsWith('\n') && !inserted.EndsWith('\r'))
		{
			inserted += LineRangeLocator.DetectNewLine(original);
		}

		String rewritten = String.Concat(original.AsSpan(0, span.Start), inserted, original.AsSpan(span.End));
		await WriteAtomically(resolved.FullPath, Encode(rewritten, hadBom), cancellationToken).ConfigureAwait(false);

		String? report = await Report(this._notifier.NotifyChangedAsync, resolved.FullPath, cancellationToken).ConfigureAwait(false);

		return new LinesReplaced(
			shown,
			endLine - startLine + 1,
			LineRangeLocator.CountLines(text),
			LineRangeLocator.CountLines(rewritten),
			Join(resolved.Note, report));
	}

	[Description("Deletes a file in the workspace, or a directory with everything under it. The workspace root itself cannot be deleted.")]
	public async Task<FileDeleted> Delete(
		[Description("A path relative to the workspace root.")] String path,
		CancellationToken cancellationToken = default)
	{
		GuardedPath resolved = this._guard.Resolve(path);
		String shown = Shown(resolved);
		if (resolved.RelativePath.Length == 0)
		{
			throw new InvalidOperationException("The workspace root itself cannot be deleted.");
		}

		if (File.Exists(resolved.FullPath))
		{
			await Retry(() => File.Delete(resolved.FullPath), cancellationToken).ConfigureAwait(false);
			String? report = await Report(this._notifier.NotifyDeletedAsync, resolved.FullPath, cancellationToken).ConfigureAwait(false);

			return new FileDeleted(shown, 1, Join(resolved.Note, report));
		}

		if (!Directory.Exists(resolved.FullPath))
		{
			throw new FileNotFoundException($"'{shown}' is not a file or directory in the workspace.", resolved.FullPath);
		}

		// Listed before anything is removed: the reports name files, one each, and a directory holding a
		// link is refused whole rather than deleted around it — every tool refuses links, and a recursive
		// delete that met one would have to decide what it meant.
		List<String> files = FilesUnder(resolved.FullPath, shown);
		await Retry(() => Directory.Delete(resolved.FullPath, recursive: true), cancellationToken).ConfigureAwait(false);

		Int32 unreported = 0;
		foreach (String file in files)
		{
			unreported += await Report(this._notifier.NotifyDeletedAsync, file, cancellationToken).ConfigureAwait(false) is null ? 0 : 1;
		}

		return new FileDeleted(
			shown,
			files.Count,
			Join(resolved.Note, unreported == 0 ? null : $"{unreported} deletion(s) were not reported to the index; it will find them on its own"));
	}

	/// <summary>
	/// The text of an existing text file and whether it carried a byte-order mark, so that a rewrite
	/// keeps the mark it found. Opened share-read, once, with no retry.
	/// </summary>
	private static (String Text, Boolean HadBom) ReadText(GuardedPath resolved, String shown)
	{
		FileInfo file = TextFile.Existing(resolved, shown);
		Byte[] bytes = File.ReadAllBytes(file.FullName);
		Boolean hadBom = bytes.AsSpan().StartsWith(Bom);

		return (Utf8WithoutBom.GetString(bytes, hadBom ? Bom.Length : 0, bytes.Length - (hadBom ? Bom.Length : 0)), hadBom);
	}

	private static Byte[] Encode(String text, Boolean withBom)
		=> withBom
			? [.. Bom, .. Utf8WithBom.GetBytes(text)]
			: Utf8WithoutBom.GetBytes(text);

	/// <summary>
	/// Writes the bytes to a temporary file beside the target and renames it over the target. The name
	/// ends in <c>.tmp</c>, which the indexing front end never reports, so the transient file costs no
	/// index pass of its own. A rename that fails past the retries leaves nothing behind.
	/// </summary>
	private static async Task WriteAtomically(String fullPath, Byte[] bytes, CancellationToken cancellationToken)
	{
		String temporary = Path.Combine(
			Path.GetDirectoryName(fullPath)!,
			$"{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

		try
		{
			await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
			await Retry(() => File.Move(temporary, fullPath, overwrite: true), cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			File.Delete(temporary);
			throw;
		}
	}

	/// <summary>
	/// Runs a rename or a delete again while a share-read open denies it, with a doubling delay between
	/// attempts. An indexer read is over in milliseconds; an editor's hold is not, and past the last
	/// attempt the failure is the caller's to report.
	/// </summary>
	private static async Task Retry(Action operation, CancellationToken cancellationToken)
	{
		TimeSpan delay = RetryFirstDelay;
		Int32 attempt = 1;
		while (true)
		{
			try
			{
				operation();
				return;
			}
			catch (Exception ex) when (attempt < RetryAttempts && ex is IOException or UnauthorizedAccessException)
			{
				await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
				delay *= 2;
				attempt++;
			}
		}
	}

	/// <summary>
	/// Tells the front end what changed. Never throws: the mutation has already happened, and a report
	/// that could not be made costs the index latency, which the note says, and nothing else.
	/// </summary>
	private static async Task<String?> Report(Func<String, CancellationToken, ValueTask> notify, String fullPath, CancellationToken cancellationToken)
	{
		try
		{
			await notify(fullPath, cancellationToken).ConfigureAwait(false);
			return null;
		}
		catch (Exception)
		{
			return "the change was not reported to the index; it will find it on its own";
		}
	}

	/// <summary>Every file under a directory, for the reports; throws at the first link met, before anything is deleted.</summary>
	private static List<String> FilesUnder(String fullPath, String shown)
	{
		List<String> files = [];
		Stack<String> pending = new();
		pending.Push(fullPath);

		while (pending.Count > 0)
		{
			foreach (FileSystemInfo info in new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos())
			{
				if (info.LinkTarget is not null)
				{
					throw new InvalidOperationException($"'{shown}' contains a link at '{info.FullName}'; links are refused by every tool, so the directory was not deleted.");
				}

				if ((info.Attributes & FileAttributes.Directory) != 0)
				{
					pending.Push(info.FullName);
				}
				else
				{
					files.Add(info.FullName);
				}
			}
		}

		return files;
	}

	private static String Shown(GuardedPath resolved)
		=> resolved.RelativePath.Length == 0 ? "." : resolved.RelativePath;

	private static String? Join(String? first, String? second)
		=> (first, second) switch
		{
			(null, null) => null,
			(null, _) => second,
			(_, null) => first,
			_ => first + "; " + second,
		};
}
