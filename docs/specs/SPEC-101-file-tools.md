# SPEC-101 — File Tools

| | |
|---|---|
| Status | Draft — containment, the read tools, the text search, the file-level semantic search and the mutation tools written and implemented |
| Version | 0.5.0 |
| Owner | Tools |
| Last updated | 2026-09-16 |

## Purpose

Give an agent a confined view of one folder: a way to list, read, search and change files that
cannot reach anything outside the folder, and cannot touch the index's own metadata.

## Scope

**In scope**

- The containment rule every caller-supplied path is resolved through, and what it refuses.
- The read tools, the text search, the file-level semantic search and the mutation tools.

**Out of scope**

- How the index ranks a query ([SPEC-110](SPEC-110-rag-retrieval.md)). The tools here that ask it
  do so through its contract and add nothing to the ranking; the passage-level search tool that
  reduces hits under a token budget is an orchestration concern
  ([SPEC-100](SPEC-100-conversation-orchestration.md)).
- How a tool failure is reported to the model. The facade that turns an exception into a string the
  model must report belongs to the agent layer, and is not built.

This version writes the containment rule, the four read tools, the text search, the file-level
semantic search and the four mutation tools. All of it is built and registered; nothing at runtime
resolves it yet.

## The rule

**A caller-supplied path is resolved to a full path and refused unless it is inside the workspace
root.** Everything else in this document is a way that rule could be defeated, and what closes it.

The workspace root is the analyzed folder — the same path the indexer runs over — and the metadata
folder is the one the index lives in ([SPEC-130](SPEC-130-persistence.md)). Both are fixed when the
guard is built; a guard is not re-pointed.

### Resolution

1. **An empty or whitespace path is refused.** It resolves to the root itself, which is a directory
   a tool might then read or delete without anyone having named it.
2. **The path is made absolute against the root.** A relative path is relative to the root, never
   to the process's working directory. `.` and `..` segments are collapsed before anything is
   compared, so `..` is not refused as a token — it is refused when the collapsed result leaves the
   root, and allowed when it does not (`sub/../file.txt` is `file.txt`).
3. **Windows device-path syntax is refused outright** — anything beginning `\\?\` or `\\.\`. That
   prefix asks the operating system to skip normalisation, which is exactly the step the previous
   point relies on.
4. **Containment is textual first:** the full path equals the root, or begins with the root followed
   by a separator. A sibling that shares a prefix (`C:\work2` against a root of `C:\work`) is outside.
   Comparison is case-insensitive on Windows and ordinal elsewhere; a case-only difference on a
   case-insensitive volume off Windows is refused, which is the safe direction to be wrong in.
5. **The metadata folder is refused for every operation, reads included.** A path whose first
   segment under the root is the metadata folder name is refused whether it names the folder itself
   or something inside it. The folder holds a database under WAL; a read that opens its files can
   block a checkpoint, and nothing an agent could want is in there. A metadata-named folder deeper
   in the tree is the user's, not ours, and is not refused.
6. **A symbolic link or junction at any segment is refused.** Every existing segment of the path
   below the root is checked, not only the last: a link two levels up redirects everything under
   it. The check is for a *redirecting* reparse point — one with a link target. Reparse points that
   redirect nothing (a cloud-file placeholder, a deduplicated file) are not refused, because refusing
   them would make a synced folder unusable while closing no escape. The root itself may be a link;
   it is the caller's choice of root, and only segments below it are checked.
7. **On Windows, a file whose hard-link names include one outside the root is refused.** A hard
   link carries no reparse bit and no link target, so the previous point cannot see it; the only way
   to know is to ask the volume for every name the file has. Names are checked against the physical
   root (see the next point), because they are reported relative to the physical volume. Elsewhere
   a hard link is indistinguishable from the file, and is allowed.
8. **A `subst`-mapped drive is seen through.** On Windows, the drive letter of the root and of the
   path are each resolved to their DOS device target. A target of the form `\??\X:\path` is a
   substitution, and the drive is replaced by its target before the two are compared; a `\Device\…`
   target is a real volume and is compared as written. This is what lets a root on a substituted
   drive accept the same file named through its physical path, and refuse a substituted drive that
   maps outside the root under a letter that looks unrelated.

   The resolution goes one level. A substitution whose target is itself a substituted drive, or a
   target of a shape this code does not recognise, is **unverifiable**: the textual comparison still
   decides, and the result carries a note saying that physical identity was not confirmed. A tool
   passes that note on; a failure to verify is not a reason to refuse a path that is inside the root
   as written, and it is not a reason to hide that the extra check did not run.
9. **A root that is a drive root works.** `C:\` and `/` are roots with no name of their own; the
   containment comparison is built so that neither the empty relative path nor the separator
   handling breaks on them.

A refusal is an exception naming which rule refused and the path as given. It is meant for the
tool facade to turn into the string the model reports; it is not a condition a tool retries.

### What the guard returns

A resolved path carries the **full path as the caller named it** (the tool opens that), the
**relative path under the root** (what a listing or a result shows, so a substituted drive letter
does not leak into output), and the **note**, when the physical check could not be completed.

### Listings

A directory listing hides the metadata folder. The guard answers whether a full path is the metadata
folder or inside it, so the one rule serves both refusing a path and hiding one.

### Two instances, one type

The read tools and the mutation tools each hold their own guard over the same root. The split is the
contract that lets a roster grant reading without writing; giving both holders one shared instance
would make that split a matter of which methods a class happens to expose.

## The read tools

Five methods on one holder, `ReadTools`, over its own guard: four readers and the text search. Every
path argument goes through `Resolve` first, so a refusal is the guard's exception and not a tool's
opinion. The rules that hold across all five:

- **A result is a record with an explicit `Note`.** A bound that cut the result, a range that ran
  past the end of the file, entries hidden from a listing — each is said in the note, and the record
  carries a `Truncated` flag where a bound applied. A tool never returns a fake hit, an invented line,
  or a silently shortened answer.
- **A hard failure is an exception, not an empty result.** A missing file, a path that is a directory
  where a file was asked for, a binary file, a bad line range — each throws its ordinary .NET
  exception. The facade that turns those into the string the model must report belongs to the agent
  layer and is not built; until then the exception is the contract.
- **Bounds are code constants**, stated below. Per-call budgets are deferred for the same reason
  the reducer's are ([SPEC-110](SPEC-110-rag-retrieval.md)): the caller is a model, and a model that
  can raise a bound will.
- **Reads open with share-read and do not retry.** Two readers coexist with the indexer's own
  share-read opens; a read that fails has met a genuine external writer, and should say so at once
  rather than wait it out.
- **Paths in results are relative to the root**, with the platform's separator and no leading
  separator; the root itself is shown as `.`. A substituted drive letter or the absolute root never
  appears in a result.

### `InspectDirectory(path)`

The direct children of one directory, not a recursive tree: name, whether it is a directory, size
for a file, last write time. Sorted by name, ordinally. The metadata folder is hidden, and so is any
entry that is a symbolic link or junction — the guard would refuse it on the next call, and listing
what cannot be opened invites the call. Hidden links are counted in the note. At most **500**
entries; past that the listing is cut, `Truncated` is set and the note says so. A path that is not an
existing directory throws.

### `ReadFile(path, startLine, endLine)`

A numbered line range. Lines are 1-based; `startLine` defaults to 1 and `endLine` to the end of the
file. The reported `TotalLines` is the file's, counted through to the end whatever the range, so the
caller knows where it stands. The rules of the cut:

- At most **400** lines per call, and at most **64,000** characters. Whichever bound hits first ends
  the result, `Truncated` is set, and the note names the next line to ask for.
- A range that runs past the end of the file is not truncation: the file ended. The result holds the
  lines that exist, `Truncated` is false, and the note says how many lines the file has.
- A `startLine` past the end returns no lines and a note; it does not throw, because asking for line
  500 of a 300-line file is a reasonable thing for a caller who has not read it yet to do.
- `startLine` below 1, or `endLine` below `startLine`, throws.

Line endings: `\n`, `\r\n` and a final line with no terminator each count as a line; an empty file
has zero. A byte-order mark is consumed, not returned as text.

### `Retrieve(path)`

The whole file as one string, bounded at **200,000** characters. `TotalBytes` is the size on disk,
so a caller can see how much of the file it got. Past the bound the text is cut, `Truncated` is set,
and the note points at `ReadFile` for the rest.

### What both file readers refuse

- **A file above 16 MB** throws, before any of it is read. Nothing a line range or a bounded read
  returns from such a file is worth reading it through, and the indexer's own bound is 1 MB.
- **A file whose first 8 KB contain a NUL byte** throws as not a text file. This is the only binary
  detection there is until the extraction registry exists; a PDF or a `.docx` is refused, not
  returned as its raw bytes.

### `FindFiles(pattern)`

A glob over the folder, matched against a **list this tool walks itself**, never by pointing a
matcher at the live tree. The walk starts at the root and takes each directory's files first, in name
order, then its subdirectories in name order — so the matches nearest the root come first; it skips the
metadata folder, the directory names the indexing scanner ignores (asked of the scanner, not
copied), and every symbolic link or junction, file or directory. Skipping links is what keeps a
walk inside the root; the guard would refuse each one individually, but a walk that followed them
would enumerate the outside before anyone asked.

The pattern's separator is `/`; a backslash is accepted and read as `/`. `*` matches within a
segment, `?` one character, `**` any number of segments. A pattern with no separator matches
**against the file name at any depth** — `*.md` finds every Markdown file — and a pattern with a
separator matches the whole relative path, so `docs/*.md` finds only the top level of `docs`.
Matching is case-insensitive on Windows and ordinal elsewhere, the same rule as containment. A
pattern that is empty, or contains a `..` segment, throws.

Results are relative paths, in walk order. At most **200** matches; the walk stops at the bound,
`Truncated` is set, and the note says so. The walk also stops after examining **100,000** entries,
with a note, so a pattern that matches nothing in an enormous tree still returns.

## The text search

### `SearchText(pattern, regex, ignoreCase, wholeWord, path, cancellationToken)`

A line-by-line scan of the folder's text files for a literal string or a regular expression, returning
each matching line with its relative path and 1-based line number — the shape of `grep -rn`. It reads
the files, not the index, which is what makes it useful for the two things semantic search is not: an
exact lookup (an identifier, an error string, a date), and the window before the first index is ready.

**Which files.** The same walk as `FindFiles` (files first, then subdirectories, both in name order;
links, the metadata folder and the scanner's ignored directory names left out), starting at `path` —
the root by default — which must resolve to an existing directory or the tool throws. Of the files
walked, only those the indexing scanner would read are searched: its extension allow-list, asked of
the scanner, and its size bound, taken from the same configuration value the scanner takes it from
(`Indexing:MaxTextFileSizeBytes`) and passed to the holder at construction. A file above the bound is
skipped and counted in the note. Every extension on that list is plain text; the extraction registry
that will say which formats can be scanned as raw lines does not exist yet, and until it does the
scanner's list is the whole answer.

**Matching.** A literal pattern is an ordinal substring; `ignoreCase` makes it ordinal-ignore-case.
With `regex` the pattern is a .NET regular expression, culture-invariant, with `IgnoreCase` when asked
and a one-second match timeout per line. `wholeWord` is decided **after** a candidate is found, by one
rule for both forms (`WordBoundary`: a word character is a letter, a digit or an underscore, and a
match is whole when the characters either side of it are not), and a candidate that fails it is passed
over — the search continues along the line, from one character on for a literal and from the next
regex match for an expression. An empty pattern, and an expression that does not parse, throw. A line is
reported once, at its first whole match, however many it holds.

**Bounded four ways, and each says so.**

- At most **200** matching lines. The bound ends the search; `Truncated` is set and the note says to
  narrow the pattern or the path.
- At most **64,000** characters of matched text in one result. The same cut, named separately in the
  note. A line longer than **400** characters is not returned whole but as a window of that many
  characters around its first match, with an ellipsis at each end that was cut; the note counts the
  lines so shortened. Without this one minified line would spend the whole payload on itself.
- A whole-search deadline of **10 seconds**, checked after each line and each file. Past it, what has
  been found is returned with `Truncated` set and a note saying how many files were searched, because
  a partial answer that says it is partial is worth more than none.
- The caller's cancellation token, checked at the same points. Cancellation is not a bound: it throws
  `OperationCanceledException`, since a caller that has cancelled does not want a partial result.

**What is skipped is counted.** A file the read fails on — held by a writer, or gone since the walk
listed it — is skipped and counted, and the search goes on: one locked file must not end a search of
the rest. The note carries the count; `Truncated` is not set, because no bound applied. Reads open
share-read, as the other tools' do, and do not retry.

The result carries `FilesSearched` — how many files were actually read — so that "no matches" over
zero files reads differently from "no matches" over three hundred.

## The search tools

A second holder, `SearchTools`, apart from the read tools for a reason that is a contract and not a
file layout: **a search tool's failure is fatal where a file tool's is a string.** A swallowed retrieval
fault is indistinguishable from "nothing relevant", and a model that believed it had searched would
answer from prior knowledge. The holder is what the facade that enforces that difference will tell the
two groups apart by. It holds no guard: it names no path a caller supplied, and the paths it returns
come from the index.

### `FindFilesAbout(query, maxFiles)`

Which files are about a topic, by meaning: each file with the score of its best passage and how many
of its passages ranked, best first. The tool exists because a question about a folder is often "where
is this discussed" before it is "what does it say", and the passage-level search answers the second.

**It goes through the composed query and nothing else.** The holder is built over the `IRetrievalQuery`
the composition root wrapped — the telemetry decorator, whichever backend the profile chose, and the
readiness guard — and asks it for passages. A file-level search that read every vector itself would be a
second retrieval path beside the measured one: unobserved on `GET /metrics`, a full scan whatever the
store, and a second place to get model scoping wrong. So this tool adds nothing to the ranking; it folds
passages into files.

- **Over-fetched, then folded.** The query is asked for `maxFiles × 5` passages, at most **100**. Passages
  are grouped by file; a file's score is its best passage's, and its `MatchingChunks` is how many of its
  passages were in the pool. Files are ordered by score, then by path for a total order.
- **`maxFiles` defaults to 10 and is cut to 20**, with the cut said in the note; below 1 throws.
- **The two ways the pool can hide a file are each said.** More files in the pool than asked for is a
  cut: `Truncated` is set and the note says how many the pool spanned. A pool that came back full with
  nothing cut is not a cut, but a file whose every passage ranks below the pool is not listed, and the
  note says so. Both are what the caller needs to decide whether to ask again with a narrower query.
- **An empty result is a result**, with a note saying no passage ranked — which is what a query against
  an index that holds no vectors for the active model produces, by `SPEC-110`'s rule that a never-indexed
  model version returns nothing.
- **Refusal passes through.** While the first index builds, or after it failed, the composed query
  throws `IndexNotReadyException`; the tool does not catch it, because a refusal is the caller's signal
  and observing it is not a licence to answer. An empty query throws.
- Paths are shown as the read tools show them: relative to the root, platform separator.

Not applied here, deliberately: the low-confidence screen and the token-budget reducer. Both belong to
the passage-level search tool, which returns text and has a budget to spend; this tool returns names and
scores, and the score is there so a caller can see a weak best match for what it is.

## The mutation tools

A third holder, `MutationTools`, over a second guard of its own (see *Two instances, one type*). Four
methods: `Create`, `Update`, `ReplaceLines`, `Delete`. Every path goes through the holder's guard first,
a hard failure is the ordinary exception, the result carries the guard's note when the physical check
could not complete, and paths are shown as the read tools show them. The rules that hold across all four:

- **Every write is a temporary file and a rename, never a write in place.** The temporary file is
  written beside the target, named `<file>.<guid>.tmp` — an ending the indexing front end never reports
  ([SPEC-121](SPEC-121-file-indexing-front-end.md)), so the transient costs no index pass — and renamed
  over the target. A write in place holds a handle the indexer's share-read opens collide with, and a
  reader that opened the file half-way through would read a torn document; a rename is one step, and the
  file is always either the old whole or the new whole. A write that fails past its retries, or is
  cancelled, leaves no temporary file behind.
- **A rename and a delete are retried; a read is not.** A file the indexer is delivering is open
  share-read, which denies a replace over it and a delete of it. The failure is reported as
  `IOException` or `UnauthorizedAccessException` depending on the platform, so both are retried:
  **6** attempts, the first delay **25 ms**, doubling — under a second in all, which outlasts an indexer
  read and not an editor's hold. Past the last attempt the exception is the caller's to report. Reads
  open share-read once, as the read holder's do: a read that fails has met a genuine external writer.
- **A completed mutation is reported to the indexing front end with its own kind** — created, changed
  or deleted, one report per file — through `IIndexChangeNotifier`, so the index follows an edit this
  process made without waiting to rediscover it. The kind is load-bearing: a create and a delete inside
  one window annihilate, and a create reported as a change reaches the same end state only by luck
  (SPEC-121). The report is **advisory**: one that fails — including one refused because the caller's
  token was cancelled after the write had happened — is said in the result's note, and never turns a
  completed write into a reported failure.
- **Encoding and line endings are the file's.** A rewrite decodes UTF-8, keeps a byte-order mark it
  found, and writes the same way. `Update` touches nothing but the occurrences; `ReplaceLines` adds a
  terminator only in the file's own form, taken from its first line ending. A new file is written as
  UTF-8 without a mark.
- **What the readers refuse, the rewriters refuse**: a file above 16 MB and a file whose first 8 KB
  hold a NUL byte, through the one check both holders share (`TextFile`).
- **Nothing is changed silently.** An `Update` whose text does not occur throws rather than reporting
  zero replacements, because the caller asked for a change and none happened; a `ReplaceLines` past
  the end of the file throws rather than appending.

### `Create(path, content)`

Writes a new file with the content, creating missing parent directories. An existing file throws —
creating over a file is an update by another name, and would be reported with the wrong kind — and so
does a directory. Reported as created; the result carries the bytes written.

### `Update(path, find, replace, ignoreCase, wholeWord)`

Replaces every occurrence of a literal — not an expression — in one pass, and returns the count.
`ignoreCase` compares ordinally without case; `wholeWord` goes through the same `WordBoundary` rule
the text search uses, so the two tools cannot mean different things by it. Matches do not overlap, and
a replacement is never searched again. The replacer keeps **two cursors** — where the next search
starts, and how much of the original has been copied to the output — and a candidate the word rule
rejects advances the first and not the second; had it moved both, the text between would be dropped
silently and the count would still be right. An empty `find` throws; an empty `replace` removes the
occurrences.

### `ReplaceLines(path, startLine, endLine, text)`

Replaces an inclusive 1-based range, numbered as `ReadFile` numbers lines, with the text verbatim. The
range runs from the first character of `startLine` past the terminator of `endLine`; the text takes its
place, and gains the file's own terminator when the range had one and the text ends in none, so what
follows stays on its own line. Empty text removes the lines, terminator included. The last line of a
file with no terminator is replaced without adding one. A range past the end throws naming the file's
line count; `startLine` below 1, or `endLine` below `startLine`, throws. The offset arithmetic is
`LineRangeLocator`, tested on its own for `\n`, `\r\n`, a lone `\r`, no trailing newline, an empty file
and a single line, because a replacement one character off still produces a file that reads plausibly.
The result says how many lines were removed, how many inserted, and how many the file has now.

### `Delete(path)`

Deletes a file, or a directory with everything under it. The root itself is refused. A directory is
listed before anything is removed, and one that holds a symbolic link or junction anywhere under it is
refused whole — every tool refuses links, and a recursive delete that met one would have to decide what
it meant. One deletion is reported per file, and the result counts them.

## Contracts

```csharp
internal sealed class WorkspacePathGuard
{
	WorkspacePathGuard(String workspaceRoot, String metadataFolderName);
	String Root { get; }
	GuardedPath Resolve(String path);            // throws WorkspaceContainmentException
	Boolean IsMetadataFolder(String fullPath);
}

internal sealed record GuardedPath(String FullPath, String RelativePath, String? Note);

internal enum ContainmentRefusal { EmptyPath, DevicePath, OutsideRoot, MetadataFolder, ReparsePoint, HardLinkOutsideRoot }

internal sealed class WorkspaceContainmentException : InvalidOperationException
{
	ContainmentRefusal Refusal { get; }
	String Path { get; }
}

internal sealed class ReadTools
{
	ReadTools(WorkspacePathGuard guard, Int64 maxSearchFileBytes);
	DirectoryListing InspectDirectory(String path);
	FileLines ReadFile(String path, Int32 startLine = 1, Int32? endLine = null);
	FileText Retrieve(String path);
	FileMatches FindFiles(String pattern);
	TextSearchResult SearchText(String pattern, Boolean regex = false, Boolean ignoreCase = false,
		Boolean wholeWord = false, String path = ".", CancellationToken cancellationToken = default);
}

internal sealed record DirectoryEntry(String Name, Boolean IsDirectory, Int64? SizeBytes, DateTime ModifiedUtc);
internal sealed record DirectoryListing(String Path, IReadOnlyList<DirectoryEntry> Entries, Boolean Truncated, String? Note);
internal sealed record NumberedLine(Int32 Number, String Text);
internal sealed record FileLines(String Path, IReadOnlyList<NumberedLine> Lines, Int32 TotalLines, Boolean Truncated, String? Note);
internal sealed record FileText(String Path, String Text, Int64 TotalBytes, Boolean Truncated, String? Note);
internal sealed record FileMatches(String Pattern, IReadOnlyList<String> Paths, Boolean Truncated, String? Note);
internal sealed record TextMatch(String Path, Int32 Line, String Text);
internal sealed record TextSearchResult(String Pattern, IReadOnlyList<TextMatch> Matches, Int32 FilesSearched, Boolean Truncated, String? Note);

internal sealed class GlobPattern
{
	static GlobPattern Parse(String pattern);   // throws ArgumentException
	Boolean IsMatch(String relativePath);       // '/'-separated
}

internal sealed class TextMatcher
{
	static TextMatcher Create(String pattern, Boolean regex, Boolean ignoreCase, Boolean wholeWord);   // throws ArgumentException
	Int32 IndexIn(String line);                 // first whole match, or -1
}

internal static class WordBoundary
{
	static Boolean IsWordChar(Char c);
	static Boolean IsWholeWord(String text, Int32 index, Int32 length);
}

internal sealed class SearchTools
{
	SearchTools(IRetrievalQuery query, String databasePath);
	FilesAbout FindFilesAbout(String query, Int32 maxFiles = 10);   // IndexNotReadyException passes through
}

internal sealed record FileRelevance(String Path, Double Score, Int32 MatchingChunks);
internal sealed record FilesAbout(String Query, IReadOnlyList<FileRelevance> Files, Boolean Truncated, String? Note);

internal sealed class MutationTools
{
	MutationTools(WorkspacePathGuard guard, IIndexChangeNotifier notifier);
	Task<FileCreated> Create(String path, String content, CancellationToken cancellationToken = default);
	Task<FileUpdated> Update(String path, String find, String replace, Boolean ignoreCase = false,
		Boolean wholeWord = false, CancellationToken cancellationToken = default);
	Task<LinesReplaced> ReplaceLines(String path, Int32 startLine, Int32 endLine, String text,
		CancellationToken cancellationToken = default);
	Task<FileDeleted> Delete(String path, CancellationToken cancellationToken = default);
}

internal sealed record FileCreated(String Path, Int64 Bytes, String? Note);
internal sealed record FileUpdated(String Path, Int32 Replacements, String? Note);
internal sealed record LinesReplaced(String Path, Int32 LinesRemoved, Int32 LinesInserted, Int32 TotalLines, String? Note);
internal sealed record FileDeleted(String Path, Int32 FilesDeleted, String? Note);

internal static class LineRangeLocator
{
	static Int32 CountLines(String text);
	static LineSpan Locate(String text, Int32 startLine, Int32 endLine);   // throws ArgumentOutOfRangeException
	static String DetectNewLine(String text);
}

internal readonly record struct LineSpan(Int32 Start, Int32 End, Boolean EndsWithTerminator);

internal static class TextReplacer
{
	static String Replace(String text, String find, String replacement, Boolean ignoreCase, Boolean wholeWord, out Int32 count);
}

internal static class TextFile
{
	const Int64 MaxBytes;                                            // 16 MB
	const Int32 SniffBytes;                                          // 8 KB
	static FileInfo Existing(GuardedPath resolved, String shown);   // throws
	static StreamReader OpenText(FileInfo file);                    // share-read, no retry
}
```

The search bounds, as constants on `SearchTools`: `MaxFiles` 20, `CandidatesPerFile` 5,
`MaxCandidates` 100.

The bounds, as constants on `ReadTools`: `MaxEntries` 500, `MaxLinesPerRead` 400,
`MaxCharsPerRead` 64,000, `MaxRetrieveChars` 200,000, `MaxFileBytes` 16 MB, `MaxMatches` 200,
`MaxExamined` 100,000, `SniffBytes` 8 KB, `MaxSearchLines` 200, `MaxSearchChars` 64,000,
`MaxSearchLineChars` 400, `DefaultSearchDeadline` 10 s. The search's file-size bound is the
constructor argument, not a constant, because it is the scanner's. `MaxFileBytes` and `SniffBytes`
are `TextFile`'s, which both file holders share; `ReadTools` re-exposes them under those names.

The mutation bounds, as constants on `MutationTools`: `RetryAttempts` 6, `RetryFirstDelay` 25 ms.

## Sharp edges

- **Time of check to time of use.** The guard inspects the path and the tool then opens it. A link
  created between the two is not seen. The window is one call wide and the actor who could exploit
  it already has write access to the folder, which is more than the guard protects.
- **A file's alternate data stream** (`file.txt:name` on NTFS) is inside the file and is not
  refused. It cannot reach outside the root; it can hold content a listing does not show.
- **Hard links off Windows** are allowed without inspection. The check has one implementation, on
  the platform the tree is developed on; a Linux host with a hard link into the root from outside
  it would read that file. A rewrite does not reach the outside name: the rename replaces the
  directory entry inside the root and the other name keeps the old content, and a delete unlinks the
  inside name only. Recorded rather than closed.
- **A rewrite races an external writer.** `Update` and `ReplaceLines` read the file, compute, and
  rename over it; an external write that lands between the read and the rename is overwritten, and
  the tool cannot tell. The window is one call wide, and the loser is a writer with access to the
  folder already — the same actor the containment guard does not protect against.
- **A directory delete that fails part-way is partly done.** The retries finish it in the ordinary
  case; past them the exception surfaces with some files gone and none of them reported, and the
  index finds them at its next reconcile.
- **A report after cancellation is dropped, and the write stands.** A caller whose token is cancelled
  once the rename has happened gets its result with a note, not an exception: the file changed, and
  saying otherwise would be the lie.
- **The text search does not sniff for binary content.** It trusts the scanner's extension list, as
  the index does; a binary file carrying a text extension is read line by line and any NUL-bearing
  line that happens to match is returned. The per-line window and the payload bound cap what that
  can cost; the file readers' NUL check is not applied because the search would then refuse files
  the index has embedded.
- **A regular expression with a catastrophic backtracking pattern** hits the one-second per-line
  timeout and throws `RegexMatchTimeoutException`, which ends the search as a hard failure rather
  than a partial result. The deadline covers slow searches, not a single line that never finishes.

## Test strategy

Direct tests of the rule, not through any tool, each staging its own fixture in a temporary folder:

- Relative and absolute paths inside the root resolve; the relative path is reported under the root.
- A collapsed `..` that stays inside resolves; one that leaves is refused; a sibling with a shared
  prefix is refused; an empty path is refused; a device-path prefix is refused.
- The metadata folder and a file inside it are refused; a same-named folder deeper in the tree is
  not.
- A junction (Windows) or symbolic link (elsewhere) at the last segment and at an intermediate
  segment is refused; a plain directory beside it is not.
- A hard link inside the root to a file outside it is refused on Windows and allowed elsewhere —
  one test, asserting the documented behaviour of the platform it runs on.
- On Windows, a `subst` drive over the root accepts the physical path; a root on a `subst` drive
  accepts the physical path and reports the relative path without the letter; a `subst` drive
  mapped outside the root is refused; a chain leaves the textual result standing and adds the note.
- A drive root as workspace root resolves a path beneath it.

**A fixture that cannot be staged fails the test.** A junction that could not be created, a `subst`
letter that could not be claimed, a hard link the volume would not make — each throws, so the test
is red rather than green with nothing asserted. The `subst` tests exist only on Windows; on another
platform they are reported as skipped with the reason, never as passed.

The read tools, each asserted on its arithmetic directly and never through an end-to-end path:

- The listing is sorted, hides the metadata folder and links and counts the links in the note, cuts
  at the entry bound with the flag set, and throws for a file or a missing directory.
- The line reader numbers `\n`, `\r\n` and an unterminated last line alike, returns zero lines for an
  empty file, consumes a byte-order mark, returns the existing lines without the flag when the range
  runs past the end, returns no lines and a note for a start past the end, cuts at the line bound
  and at the character bound with the next line named, refuses a NUL-bearing file and an oversize
  one, and throws for a bad range.
- The whole-file reader returns a small file entire and cuts a large one with the size on disk
  reported.
- The glob matcher is tested as a table: each pattern form against paths that should and should not
  match, including the no-separator-matches-any-depth rule and case handling per platform.
- The walk excludes the ignored directory names, the metadata folder, a linked directory and a
  linked file, stops at the match bound with the flag set, and refuses a `..` pattern. The linked-file
  case needs a file symbolic link, which Windows grants only to an elevated process or a machine with
  Developer Mode on; where the probe fails, that one test is reported as skipped with the reason,
  never as passed. CI's Windows runner is elevated, so there it runs.

The text search, its matcher first and the tool over it:

- The matcher as a table: literal, case-insensitive, whole-word, regex and their combinations, each
  against lines that should and should not match, asserting the **index** reported — including an
  overlapping candidate (`aa` in `aaa aa` is whole at 4), an anchored expression whose only
  candidate fails the word rule (`^the` in `these the` is no match), and a zero-length regex match.
  An empty pattern and an unparseable expression throw; a literal that looks like a broken
  expression does not.
- The tool: matches come back with file, line and text in walk order, one per line; only the
  scanner's extensions are read and the walk's exclusions hold; `path` narrows the search and a
  missing directory, a file, or a refused path throws; each flag reaches the matcher; a long line is
  shortened around the match with the note counting it; the line bound and the character bound each
  cut with their own note; a zero deadline returns a partial result with the flag and note; a
  cancelled token throws; an oversize file and a file held by a writer are skipped and counted; a
  byte-order mark does not reach a matched line.
- Mutation kills, each restored byte-for-byte: the whole-word rule removed fails the whole-word table
  rows and the search's whole-word case; the extension check removed fails exactly the
  scanner-extensions test; the per-line deadline check removed fails exactly the deadline test; the
  link skip removed from the walk fails exactly the two walk tests, `FindFiles`'s and the search's.

The file-level semantic search, over a query that answers with fixed hits:

- Passages fold into files scored by their best passage — placed deliberately not first among the
  file's hits — and counted; equal scores order by path; the composed query is called once with the
  database path, the query text and the over-fetched `k`; the pool cap and the `maxFiles` cut are
  applied and said; more files than asked for are cut with the flag and the count; a full pool with
  nothing cut carries its own note; no hits is an empty result with a note; a not-ready refusal passes
  through unchanged; an empty query and a zero count throw.
- A host test resolves the holder from the real composition root over an indexed folder and asserts
  it answers with that folder's files — the wiring to the wrapped query and the bootstrapped database
  is the thing no unit test can reach.
- Mutation kills, each restored byte-for-byte: the over-fetch removed fails three tests; the path
  tiebreak inverted fails exactly the tie test; the best-passage fold replaced by the first passage
  fails the fold test — a first fixture that listed each file's best passage first let that mutation
  live, which is why the fixture says what it does.

The mutation tools, their arithmetic first and the tools over it, asserted on the bytes left on disk
and the reports made — never through an index that would still look right after a torn write:

- The locator as a table: the line count for `\n`, `\r\n`, a lone `\r`, a trailing terminator, an
  empty text and blank lines; the span offsets for a middle range, a CRLF range, the last line with and
  without a terminator, a single-line text and the whole text; a range past the end throws naming the
  count and the parameter; the detected newline is the text's first, or the platform's.
- The replacer as a table: every occurrence replaced and counted, case, whole word, an overlap
  (`aa` in `aaa` once), a replacement containing the pattern not re-matched, an empty replacement,
  and — its own test — a candidate the word rule rejects between two matches, whose text must reach
  the output.
- The tools: `Create` writes the bytes, makes the parents, reports a creation with the full path and
  leaves no temporary file; it refuses an existing file, a directory, a path outside and the metadata
  folder with nothing written and nothing reported. `Update` replaces and counts, keeps a byte-order
  mark and CRLF endings, throws on no occurrence with the file's write time unchanged and nothing
  reported, passes both flags through, and refuses a missing file, a NUL-bearing file and an empty
  pattern. `ReplaceLines` keeps the following line on its own line, uses the file's own ending, adds
  none after a last line that had none, removes lines for empty text, replaces a single-line file
  whole, and throws for a range past the end, an empty file, a zero start and an inverted range with
  the file unchanged. `Delete` removes a file and reports it, removes a directory and reports each
  file in it, and refuses the root, a missing path and a directory holding a link with nothing removed
  on either side of the link.
- A write and a delete wait out a reader holding the file share-read. On Windows that is the
  property: the rename and the delete are denied until the handle closes, and the test holds it past
  the first retries. Elsewhere both succeed at once and the test asserts the same outcome.
- A failing report is a note on each of the three results, with the mutation done. A write cancelled
  before it starts throws, changes nothing and leaves no temporary file.
- A host test resolves the holder from the real composition root, writes into the analyzed folder
  through it, is refused the metadata folder, and is a different object from the read holder.
- Mutation kills, each restored byte-for-byte: the copy cursor moved along with the scan cursor on a
  rejected candidate fails five tests (the two-cursor test, three whole-word rows and the tool's
  flag test); the retry attempts set to one fails exactly the two held-reader tests; `\r\n` unpaired
  in the locator fails four (two count rows, the CRLF span row and the tool's line-ending test); the
  appended terminator dropped fails exactly the two `ReplaceLines` tests that keep a following line;
  the link refusal turned into a count fails exactly the link test. Two first attempts only failed to
  compile under the zero-warning gate and were discarded as non-evidence.

## Open questions

- Whether an application-execution link (the reparse tag Windows Store and WSL use for a
  launcher) should be refused. It has no link target as .NET reports it, and it redirects to an
  executable outside the root. No document folder is expected to hold one.
- Whether hard links should be checked on Linux through the inode's link count. A count above one
  says the file has another name; it does not say where. Reopen when a Linux host is a real target.

## Related specs

- [SPEC-000 — System Concept](SPEC-000-system-concept.md)
- [SPEC-100 — Conversation Orchestration](SPEC-100-conversation-orchestration.md)
- [SPEC-130 — Persistence](SPEC-130-persistence.md)

## Changelog

- **0.5.0** (2026-09-16) — the mutation tools, `Create`, `Update`, `ReplaceLines` and `Delete`, in
  their own holder over a second guard: every write a temporary file and a retried rename, every
  delete retried, reads not; a literal replace with two cursors and the shared whole-word rule; a line
  range located by one directly tested rule; each completed mutation reported to the front end with
  its own kind, advisorily. The file readers' size bound and sniff moved to `TextFile`, shared by both
  holders. Four sharp edges added.
- **0.4.0** (2026-09-16) — the file-level semantic search, `FindFilesAbout`, in its own holder over
  the composed retrieval query: over-fetched passages folded into files scored by their best passage,
  bounded and said; refusal passes through; no ranking of its own. The scope line on semantic search
  narrowed to the ranking itself, which stays SPEC-110's.
- **0.3.0** (2026-09-15) — the text search: a literal or regex line scan over the scanner's own
  extensions and size bound, on the same walk as `FindFiles`; whole-word through one shared rule;
  bounded by matching lines, matched characters, a whole-search deadline and the caller's token,
  each said in the note. The holder now takes the scanner's size bound at construction.
- **0.2.0** (2026-09-15) — the four read tools: listing, numbered line range, bounded whole file,
  glob over a self-walked list. Bounds as code constants; notes for every cut; exceptions for every
  hard failure. The holder is composed in `Program.cs` and held by nothing.
- **0.1.0** (2026-09-15) — the containment rule, written with its implementation. The tools that
  will use it are named and not described.
