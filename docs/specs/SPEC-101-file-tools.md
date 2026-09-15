# SPEC-101 — File Tools

| | |
|---|---|
| Status | Draft — containment and the read tools written and implemented; text search and mutation not yet |
| Version | 0.2.0 |
| Owner | Tools |
| Last updated | 2026-09-15 |

## Purpose

Give an agent a confined view of one folder: a way to list, read, search and change files that
cannot reach anything outside the folder, and cannot touch the index's own metadata.

## Scope

**In scope**

- The containment rule every caller-supplied path is resolved through, and what it refuses.
- The read tools, the text search and the mutation tools, once they exist.

**Out of scope**

- Semantic search over the index ([SPEC-110](SPEC-110-rag-retrieval.md)); the tool that calls it
  is an orchestration concern ([SPEC-100](SPEC-100-conversation-orchestration.md)).
- How a tool failure is reported to the model. The facade that turns an exception into a string the
  model must report belongs to the agent layer, and is not built.

This version writes the containment rule and the four read tools. The text search and the mutation
tools are not built; they are named and not described.

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

Four methods on one holder, `ReadTools`, over its own guard. Every path argument goes through
`Resolve` first, so a refusal is the guard's exception and not a tool's opinion. The rules that hold
across all four:

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
	ReadTools(WorkspacePathGuard guard);
	DirectoryListing InspectDirectory(String path);
	FileLines ReadFile(String path, Int32 startLine = 1, Int32? endLine = null);
	FileText Retrieve(String path);
	FileMatches FindFiles(String pattern);
}

internal sealed record DirectoryEntry(String Name, Boolean IsDirectory, Int64? SizeBytes, DateTime ModifiedUtc);
internal sealed record DirectoryListing(String Path, IReadOnlyList<DirectoryEntry> Entries, Boolean Truncated, String? Note);
internal sealed record NumberedLine(Int32 Number, String Text);
internal sealed record FileLines(String Path, IReadOnlyList<NumberedLine> Lines, Int32 TotalLines, Boolean Truncated, String? Note);
internal sealed record FileText(String Path, String Text, Int64 TotalBytes, Boolean Truncated, String? Note);
internal sealed record FileMatches(String Pattern, IReadOnlyList<String> Paths, Boolean Truncated, String? Note);

internal sealed class GlobPattern
{
	static GlobPattern Parse(String pattern);   // throws ArgumentException
	Boolean IsMatch(String relativePath);       // '/'-separated
}
```

The bounds, as constants on `ReadTools`: `MaxEntries` 500, `MaxLinesPerRead` 400,
`MaxCharsPerRead` 64,000, `MaxRetrieveChars` 200,000, `MaxFileBytes` 16 MB, `MaxMatches` 200,
`MaxExamined` 100,000, `SniffBytes` 8 KB.

## Sharp edges

- **Time of check to time of use.** The guard inspects the path and the tool then opens it. A link
  created between the two is not seen. The window is one call wide and the actor who could exploit
  it already has write access to the folder, which is more than the guard protects.
- **A file's alternate data stream** (`file.txt:name` on NTFS) is inside the file and is not
  refused. It cannot reach outside the root; it can hold content a listing does not show.
- **Hard links off Windows** are allowed without inspection. The check has one implementation, on
  the platform the tree is developed on; a Linux host with a hard link into the root from outside
  it would read that file. Recorded rather than closed, because the tools do not yet exist to make
  the exposure real.

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

- **0.2.0** (2026-09-15) — the four read tools: listing, numbered line range, bounded whole file,
  glob over a self-walked list. Bounds as code constants; notes for every cut; exceptions for every
  hard failure. The holder is composed in `Program.cs` and held by nothing.
- **0.1.0** (2026-09-15) — the containment rule, written with its implementation. The tools that
  will use it are named and not described.
