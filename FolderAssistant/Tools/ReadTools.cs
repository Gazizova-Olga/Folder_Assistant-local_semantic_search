using System.ComponentModel;
using System.Diagnostics;
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
/// One matching line of a text search: the file, the 1-based line number, and the line's text —
/// shortened to a window around the first match when the line is long.
/// </summary>
internal sealed record TextMatch(String Path, Int32 Line, String Text);

/// <summary>
/// The lines a text search matched, in walk order. <paramref name="FilesSearched"/> is how many files
/// were read; <paramref name="Truncated"/> is set when a bound or the deadline ended the search early.
/// </summary>
internal sealed record TextSearchResult(String Pattern, IReadOnlyList<TextMatch> Matches, Int32 FilesSearched, Boolean Truncated, String? Note);

/// <summary>
/// The read tools (SPEC-101): list a directory, read a line range, read a bounded whole file, find files
/// by glob, search text by literal or regular expression. Every path goes through the holder's own
/// <see cref="WorkspacePathGuard"/> first.
///
/// <para>
/// Two contracts hold across all five. A bound that cut a result, or a condition the caller should know
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
	internal const Int64 MaxFileBytes = TextFile.MaxBytes;
	internal const Int32 MaxMatches = 200;
	internal const Int32 MaxExamined = 100_000;
	internal const Int32 SniffBytes = TextFile.SniffBytes;
	internal const Int32 MaxSearchLines = 200;
	internal const Int32 MaxSearchChars = 64_000;
	internal const Int32 MaxSearchLineChars = 400;
	internal static readonly TimeSpan DefaultSearchDeadline = TimeSpan.FromSeconds(10);

	private readonly WorkspacePathGuard _guard;
	private readonly Int64 _maxSearchFileBytes;
	private readonly TimeSpan _searchDeadline;

	/// <param name="guard">The holder's own containment guard.</param>
	/// <param name="maxSearchFileBytes">
	/// The size above which a file is not searched: the indexing scanner's own bound, so the search and
	/// the index read the same files.
	/// </param>
	public ReadTools(WorkspacePathGuard guard, Int64 maxSearchFileBytes)
		: this(guard, maxSearchFileBytes, DefaultSearchDeadline)
	{
	}

	/// <summary>The deadline is a parameter here so a test can make it fire without waiting ten seconds.</summary>
	internal ReadTools(WorkspacePathGuard guard, Int64 maxSearchFileBytes, TimeSpan searchDeadline)
	{
		ArgumentNullException.ThrowIfNull(guard);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSearchFileBytes);
		this._guard = guard;
		this._maxSearchFileBytes = maxSearchFileBytes;
		this._searchDeadline = searchDeadline;
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

		// Matched against the list the walk builds: the matcher never sees the live tree.
		foreach (WalkEntry entry in this.Walk(this._guard.Root, String.Empty))
		{
			if (++examined > MaxExamined)
			{
				exhausted = true;
				break;
			}

			if (entry.IsDirectory || !glob.IsMatch(entry.Relative))
			{
				continue;
			}

			if (matches.Count == MaxMatches)
			{
				truncated = true;
				break;
			}

			matches.Add(Shown(entry.Relative));
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

	[Description("Searches the text files in the workspace for a literal string or a regular expression, line by line, and returns each matching line with its file and line number. Reads the folder directly, so it works before the index is ready and finds exact text the index would not. Bounded; the note says what was cut.")]
	public TextSearchResult SearchText(
		[Description("The text to find, or a .NET regular expression when regex is true.")] String pattern,
		[Description("Read the pattern as a regular expression. Defaults to a literal string.")] Boolean regex = false,
		[Description("Match without regard to case.")] Boolean ignoreCase = false,
		[Description("Match only where the pattern is not joined to a letter, digit or underscore on either side.")] Boolean wholeWord = false,
		[Description("The directory to search under, relative to the workspace root; '.' for the whole workspace.")] String path = ".",
		CancellationToken cancellationToken = default)
	{
		TextMatcher matcher = TextMatcher.Create(pattern, regex, ignoreCase, wholeWord);
		GuardedPath resolved = this._guard.Resolve(path);
		if (!Directory.Exists(resolved.FullPath))
		{
			throw new DirectoryNotFoundException($"'{Shown(resolved)}' is not a directory in the workspace.");
		}

		TextSearchRun run = new(matcher, this._maxSearchFileBytes, this._searchDeadline, cancellationToken);
		foreach (WalkEntry entry in this.Walk(resolved.FullPath, resolved.RelativePath.Replace(Path.DirectorySeparatorChar, '/')))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (entry.IsDirectory || !LocalTextFileScanner.IsIndexableExtension(entry.Info.Extension))
			{
				continue;
			}

			if (!run.SearchFile((FileInfo)entry.Info, Shown(entry.Relative)))
			{
				break;
			}
		}

		return run.Result(pattern);
	}

	/// <summary>
	/// One text search from start to finish: the bounds, the counters, and the reason it stopped. Kept
	/// apart from the tool method so that each bound is one line where it is checked.
	/// </summary>
	private sealed class TextSearchRun
	{
		private readonly TextMatcher _matcher;
		private readonly Int64 _maxFileBytes;
		private readonly TimeSpan _deadline;
		private readonly CancellationToken _cancellation;
		private readonly Stopwatch _clock = Stopwatch.StartNew();
		private readonly List<TextMatch> _matches = [];
		private Int32 _filesSearched;
		private Int32 _oversize;
		private Int32 _unreadable;
		private Int32 _shortened;
		private Int32 _chars;
		private Boolean _cutByLines;
		private Boolean _cutByChars;
		private Boolean _timedOut;

		public TextSearchRun(TextMatcher matcher, Int64 maxFileBytes, TimeSpan deadline, CancellationToken cancellation)
		{
			this._matcher = matcher;
			this._maxFileBytes = maxFileBytes;
			this._deadline = deadline;
			this._cancellation = cancellation;
		}

		/// <summary>Searches one file; returns false when a bound or the deadline has ended the whole search.</summary>
		public Boolean SearchFile(FileInfo file, String shownPath)
		{
			if (file.Length > this._maxFileBytes)
			{
				this._oversize++;
				return true;
			}

			this._filesSearched++;
			try
			{
				this.SearchLines(file, shownPath);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// Held by a writer, or gone since the walk listed it. One file that cannot be read must not end
				// a search of the rest; it is counted and said in the note.
				this._unreadable++;
			}

			this._timedOut |= this._clock.Elapsed >= this._deadline;

			return !(this._cutByLines || this._cutByChars || this._timedOut);
		}

		public TextSearchResult Result(String pattern)
		{
			List<String> notes = [];
			if (this._cutByLines)
			{
				notes.Add($"cut at {MaxSearchLines} matching lines; narrow the pattern or the path for the rest");
			}

			if (this._cutByChars)
			{
				notes.Add($"cut at {MaxSearchChars:N0} characters of matches; narrow the pattern or the path for the rest");
			}

			if (this._timedOut)
			{
				notes.Add($"stopped at the {this._deadline.TotalSeconds:0.#} s deadline after {this._filesSearched} file(s); the results are partial, narrow the path");
			}

			if (this._shortened > 0)
			{
				notes.Add($"{this._shortened} line(s) shortened to {MaxSearchLineChars} characters around the match");
			}

			if (this._oversize > 0)
			{
				notes.Add($"{this._oversize} file(s) above the {this._maxFileBytes:N0}-byte bound skipped");
			}

			if (this._unreadable > 0)
			{
				notes.Add($"{this._unreadable} file(s) could not be read: held by a writer, or gone");
			}

			return new TextSearchResult(
				pattern,
				this._matches,
				this._filesSearched,
				this._cutByLines || this._cutByChars || this._timedOut,
				Join(notes));
		}

		private void SearchLines(FileInfo file, String shownPath)
		{
			using StreamReader reader = OpenText(file);
			String? text;
			Int32 number = 0;
			while ((text = reader.ReadLine()) is not null)
			{
				number++;
				this._cancellation.ThrowIfCancellationRequested();

				Int32 index = this._matcher.IndexIn(text);
				if (index >= 0 && !this.Add(shownPath, number, text, index))
				{
					return;
				}

				if (this._clock.Elapsed >= this._deadline)
				{
					this._timedOut = true;
					return;
				}
			}
		}

		/// <summary>Records a matching line; returns false when a bound refused it and the search is over.</summary>
		private Boolean Add(String shownPath, Int32 number, String text, Int32 index)
		{
			if (this._matches.Count == MaxSearchLines)
			{
				this._cutByLines = true;
				return false;
			}

			String shown = Window(text, index, out Boolean shortened);
			if (this._matches.Count > 0 && this._chars + shown.Length > MaxSearchChars)
			{
				this._cutByChars = true;
				return false;
			}

			if (shortened)
			{
				this._shortened++;
			}

			this._chars += shown.Length;
			this._matches.Add(new TextMatch(shownPath, number, shown));

			return true;
		}

		/// <summary>
		/// A matched line as it is shown: whole when it fits, otherwise the window of
		/// <see cref="MaxSearchLineChars"/> around the first match with an ellipsis at each cut end. One
		/// minified line would otherwise spend the whole payload bound on itself.
		/// </summary>
		private static String Window(String line, Int32 matchIndex, out Boolean shortened)
		{
			if (line.Length <= MaxSearchLineChars)
			{
				shortened = false;
				return line;
			}

			Int32 start = Math.Max(0, matchIndex - MaxSearchLineChars / 4);
			Int32 end = Math.Min(line.Length, start + MaxSearchLineChars);
			start = Math.Max(0, end - MaxSearchLineChars);
			shortened = true;

			return (start > 0 ? "…" : String.Empty) + line[start..end] + (end < line.Length ? "…" : String.Empty);
		}
	}

	/// <summary>One entry a walk yielded: the entry, its '/'-separated path under the root, and whether it is a directory.</summary>
	private readonly record struct WalkEntry(FileSystemInfo Info, String Relative, Boolean IsDirectory);

	/// <summary>
	/// Every entry under <paramref name="fullPath"/> that a tool may see: each directory's files first, in
	/// name order, then its subdirectories in name order, so what is nearest the root comes first. Links,
	/// the metadata folder and the directory names the indexing scanner ignores are left out and not
	/// descended into. The list is this walk's own — nothing here follows a link, so a walk stays inside the
	/// root without asking the guard about each entry.
	/// </summary>
	private IEnumerable<WalkEntry> Walk(String fullPath, String relativePath)
	{
		Stack<(String Full, String Relative)> pending = new();
		pending.Push((fullPath, relativePath));

		while (pending.Count > 0)
		{
			(String full, String relative) = pending.Pop();
			List<(String Full, String Relative)> subdirectories = [];

			foreach (FileSystemInfo info in new DirectoryInfo(full).EnumerateFileSystemInfos().OrderBy(info => info.Name, StringComparer.Ordinal))
			{
				if (info.LinkTarget is not null || this._guard.IsMetadataFolder(info.FullName))
				{
					continue;
				}

				String childRelative = relative.Length == 0 ? info.Name : relative + "/" + info.Name;
				Boolean isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
				if (isDirectory)
				{
					if (LocalTextFileScanner.IgnoredDirectories.Contains(info.Name))
					{
						continue;
					}

					subdirectories.Add((info.FullName, childRelative));
				}

				yield return new WalkEntry(info, childRelative, isDirectory);
			}

			// Pushed in reverse so the stack pops them in name order.
			for (Int32 i = subdirectories.Count - 1; i >= 0; i--)
			{
				pending.Push(subdirectories[i]);
			}
		}
	}

	private static FileInfo ExistingTextFile(GuardedPath resolved)
		=> TextFile.Existing(resolved, Shown(resolved));

	private static StreamReader OpenText(FileInfo file)
		=> TextFile.OpenText(file);

	private static String Shown(GuardedPath resolved)
		=> resolved.RelativePath.Length == 0 ? "." : resolved.RelativePath;

	/// <summary>A walk's '/'-separated relative path as a result shows it: the platform's separator.</summary>
	private static String Shown(String walkRelative)
		=> walkRelative.Replace('/', Path.DirectorySeparatorChar);

	private static String? Join(List<String> notes)
		=> notes.Count == 0 ? null : String.Join("; ", notes);
}
