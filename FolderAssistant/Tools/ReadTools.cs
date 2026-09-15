using System.ComponentModel;
using System.Text;
using FolderAssistant.Indexing;

namespace FolderAssistant.Tools;

/// <summary>One child of a directory.</summary>
internal sealed record DirectoryEntry(String Name, Boolean IsDirectory, Int64? SizeBytes, DateTime ModifiedUtc);

/// <summary>The direct children of one directory. <paramref name="Note"/> says what was hidden or cut.</summary>
internal sealed record DirectoryListing(String Path, IReadOnlyList<DirectoryEntry> Entries, Boolean Truncated, String? Note);

/// <summary>One line of a file, 1-based.</summary>
internal sealed record NumberedLine(Int32 Number, String Text);

/// <summary>
/// A line range of a file. <paramref name="TotalLines"/> is the file's, whatever the range;
/// <paramref name="Truncated"/> is set only when a bound cut the requested range short.
/// </summary>
internal sealed record FileLines(String Path, IReadOnlyList<NumberedLine> Lines, Int32 TotalLines, Boolean Truncated, String? Note);

/// <summary>A whole file, or as much of it as the bound allows; <paramref name="TotalBytes"/> is the size on disk.</summary>
internal sealed record FileText(String Path, String Text, Int64 TotalBytes, Boolean Truncated, String? Note);

/// <summary>The relative paths a glob matched, in walk order.</summary>
internal sealed record FileMatches(String Pattern, IReadOnlyList<String> Paths, Boolean Truncated, String? Note);

/// <summary>
/// The read tools (SPEC-101): list a directory, read a line range, read a bounded whole file, find files
/// by glob. Every path goes through the holder's own <see cref="WorkspacePathGuard"/> first.
///
/// <para>
/// Two contracts hold across all four. A bound that cut a result, or a condition the caller should know
/// about, is said in the result's note with the flag set — never a silently shorter answer and never an
/// invented one. A hard failure — a missing file, a binary file, a bad range, a refused path — is the
/// ordinary exception, because the layer that turns it into the string the model must report is the
/// agent's, not this one's.
/// </para>
/// </summary>
internal sealed class ReadTools
{
	internal const Int32 MaxEntries = 500;
	internal const Int32 MaxLinesPerRead = 400;
	internal const Int32 MaxCharsPerRead = 64_000;
	internal const Int32 MaxRetrieveChars = 200_000;
	internal const Int64 MaxFileBytes = 16L * 1024 * 1024;
	internal const Int32 MaxMatches = 200;
	internal const Int32 MaxExamined = 100_000;
	internal const Int32 SniffBytes = 8 * 1024;

	private readonly WorkspacePathGuard _guard;

	public ReadTools(WorkspacePathGuard guard)
	{
		ArgumentNullException.ThrowIfNull(guard);
		this._guard = guard;
	}

	[Description("Lists the direct children of a directory in the workspace: name, kind, size and last write time. Not recursive.")]
	public DirectoryListing InspectDirectory(
		[Description("A path relative to the workspace root; '.' for the root itself.")] String path)
	{
		GuardedPath resolved = this._guard.Resolve(path);
		DirectoryInfo directory = new(resolved.FullPath);
		if (!directory.Exists)
		{
			throw new DirectoryNotFoundException($"'{Shown(resolved)}' is not a directory in the workspace.");
		}

		List<DirectoryEntry> entries = [];
		Int32 hiddenLinks = 0;
		Boolean truncated = false;

		foreach (FileSystemInfo info in directory.EnumerateFileSystemInfos().OrderBy(info => info.Name, StringComparer.Ordinal))
		{
			if (this._guard.IsMetadataFolder(info.FullName))
			{
				continue;
			}

			if (info.LinkTarget is not null)
			{
				hiddenLinks++;
				continue;
			}

			if (entries.Count == MaxEntries)
			{
				truncated = true;
				break;
			}

			Boolean isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
			entries.Add(new DirectoryEntry(
				info.Name,
				isDirectory,
				isDirectory ? null : ((FileInfo)info).Length,
				info.LastWriteTimeUtc));
		}

		List<String> notes = [];
		if (hiddenLinks > 0)
		{
			notes.Add($"{hiddenLinks} link(s) hidden: links are refused by every tool");
		}

		if (truncated)
		{
			notes.Add($"listing cut at {MaxEntries} entries; the directory has more");
		}

		return new DirectoryListing(Shown(resolved), entries, truncated, Join(notes));
	}

	[Description("Reads a numbered range of lines from a text file in the workspace. Lines are 1-based. Bounded; the note says where to continue.")]
	public FileLines ReadFile(
		[Description("A path relative to the workspace root.")] String path,
		[Description("The first line to return, 1-based. Defaults to 1.")] Int32 startLine = 1,
		[Description("The last line to return, inclusive. Defaults to the end of the file.")] Int32? endLine = null)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(startLine, 1);
		if (endLine is not null)
		{
			ArgumentOutOfRangeException.ThrowIfLessThan(endLine.Value, startLine);
		}

		GuardedPath resolved = this._guard.Resolve(path);
		FileInfo file = ExistingTextFile(resolved);

		Int32 requestedEnd = endLine ?? Int32.MaxValue;
		Int32 boundedEnd = startLine > Int32.MaxValue - MaxLinesPerRead + 1
			? Int32.MaxValue
			: Math.Min(requestedEnd, startLine + MaxLinesPerRead - 1);

		List<NumberedLine> lines = [];
		Int32 total = 0;
		Int32 chars = 0;
		Boolean cutByLines = false;
		Boolean cutByChars = false;

		using (StreamReader reader = OpenText(file))
		{
			String? text;
			while ((text = reader.ReadLine()) is not null)
			{
				total++;
				if (total < startLine || total > requestedEnd)
				{
					continue;
				}

				if (total > boundedEnd)
				{
					cutByLines = true;
					continue;
				}

				if (cutByChars)
				{
					continue;
				}

				if (lines.Count > 0 && chars + text.Length > MaxCharsPerRead)
				{
					cutByChars = true;
					continue;
				}

				chars += text.Length;
				lines.Add(new NumberedLine(total, text));
			}
		}

		List<String> notes = [];
		Boolean truncated = cutByLines || cutByChars;
		if (truncated)
		{
			Int32 next = lines[^1].Number + 1;
			String why = cutByChars ? $"{MaxCharsPerRead:N0} characters" : $"{MaxLinesPerRead} lines";
			notes.Add($"cut at {why}; continue from line {next} (the file has {total} lines)");
		}
		else if (lines.Count == 0)
		{
			notes.Add(total == 0
				? "the file is empty"
				: $"nothing at or after line {startLine}: the file has {total} lines");
		}
		else if (endLine is not null && endLine.Value > total)
		{
			notes.Add($"the file ends at line {total}");
		}

		return new FileLines(Shown(resolved), lines, total, truncated, Join(notes));
	}

	[Description("Reads a whole text file from the workspace, up to a bound. Prefer ReadFile with a line range for anything large.")]
	public FileText Retrieve(
		[Description("A path relative to the workspace root.")] String path)
	{
		GuardedPath resolved = this._guard.Resolve(path);
		FileInfo file = ExistingTextFile(resolved);

		Char[] buffer = new Char[MaxRetrieveChars];
		Int32 read;
		Boolean truncated;
		using (StreamReader reader = OpenText(file))
		{
			read = reader.ReadBlock(buffer, 0, buffer.Length);
			truncated = reader.Peek() >= 0;
		}

		String? note = truncated
			? $"cut at {MaxRetrieveChars:N0} characters of a {file.Length:N0}-byte file; use ReadFile with a line range for the rest"
			: null;

		return new FileText(Shown(resolved), new String(buffer, 0, read), file.Length, truncated, note);
	}

	[Description("Finds files in the workspace by glob. '*' matches within a name, '?' one character, '**' any directories. A pattern without '/' matches the file name at any depth.")]
	public FileMatches FindFiles(
		[Description("A glob such as '*.md', 'docs/*.md' or 'src/**/*.cs'.")] String pattern)
	{
		GlobPattern glob = GlobPattern.Parse(pattern);
		List<String> matches = [];
		Int32 examined = 0;
		Boolean truncated = false;
		Boolean exhausted = false;

		// Depth-first in name order, on a list this walk builds: the matcher never sees the live tree.
		Stack<(String Full, String Relative)> pending = new();
		pending.Push((this._guard.Root, String.Empty));

		while (pending.Count > 0 && !truncated && !exhausted)
		{
			(String full, String relative) = pending.Pop();
			List<(String Full, String Relative)> subdirectories = [];

			foreach (FileSystemInfo info in new DirectoryInfo(full).EnumerateFileSystemInfos().OrderBy(info => info.Name, StringComparer.Ordinal))
			{
				if (++examined > MaxExamined)
				{
					exhausted = true;
					break;
				}

				if (info.LinkTarget is not null || this._guard.IsMetadataFolder(info.FullName))
				{
					continue;
				}

				String childRelative = relative.Length == 0 ? info.Name : relative + "/" + info.Name;
				if ((info.Attributes & FileAttributes.Directory) != 0)
				{
					if (!LocalTextFileScanner.IgnoredDirectories.Contains(info.Name))
					{
						subdirectories.Add((info.FullName, childRelative));
					}

					continue;
				}

				if (!glob.IsMatch(childRelative))
				{
					continue;
				}

				if (matches.Count == MaxMatches)
				{
					truncated = true;
					break;
				}

				matches.Add(childRelative.Replace('/', Path.DirectorySeparatorChar));
			}

			// Pushed in reverse so the stack pops them in name order.
			for (Int32 i = subdirectories.Count - 1; i >= 0; i--)
			{
				pending.Push(subdirectories[i]);
			}
		}

		List<String> notes = [];
		if (truncated)
		{
			notes.Add($"cut at {MaxMatches} matches; narrow the pattern for the rest");
		}

		if (exhausted)
		{
			notes.Add($"the walk stopped after examining {MaxExamined:N0} entries; the folder has more");
		}

		return new FileMatches(pattern, matches, truncated || exhausted, Join(notes));
	}

	/// <summary>
	/// The file behind a resolved path, or the exception that says why it cannot be read: not there, a
	/// directory, above the size bound, or — by its first bytes — not text.
	/// </summary>
	private static FileInfo ExistingTextFile(GuardedPath resolved)
	{
		FileInfo file = new(resolved.FullPath);
		if (!file.Exists)
		{
			if (Directory.Exists(resolved.FullPath))
			{
				throw new FileNotFoundException($"'{Shown(resolved)}' is a directory, not a file.", resolved.FullPath);
			}

			throw new FileNotFoundException($"'{Shown(resolved)}' is not a file in the workspace.", resolved.FullPath);
		}

		if (file.Length > MaxFileBytes)
		{
			throw new IOException($"'{Shown(resolved)}' is {file.Length:N0} bytes, above the {MaxFileBytes:N0}-byte bound.");
		}

		Byte[] sniff = new Byte[SniffBytes];
		Int32 read;
		using (FileStream stream = new(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
		{
			read = stream.Read(sniff, 0, sniff.Length);
		}

		if (Array.IndexOf(sniff, (Byte)0, 0, read) >= 0)
		{
			throw new InvalidDataException($"'{Shown(resolved)}' is not a text file.");
		}

		return file;
	}

	private static StreamReader OpenText(FileInfo file)
		=> new(
			new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read),
			Encoding.UTF8,
			detectEncodingFromByteOrderMarks: true);

	private static String Shown(GuardedPath resolved)
		=> resolved.RelativePath.Length == 0 ? "." : resolved.RelativePath;

	private static String? Join(List<String> notes)
		=> notes.Count == 0 ? null : String.Join("; ", notes);
}
