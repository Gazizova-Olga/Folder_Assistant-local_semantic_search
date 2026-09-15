# SPEC-101 — File Tools

| | |
|---|---|
| Status | Draft — containment written and implemented; the tools themselves not yet |
| Version | 0.1.0 |
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

This version writes the containment rule only. The tools are not built; nothing below describes
them beyond naming the split they will have.

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

internal sealed class WorkspaceContainmentException : Exception
{
	ContainmentRefusal Refusal { get; }
	String Path { get; }
}
```

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

- **0.1.0** (2026-09-15) — the containment rule, written with its implementation. The tools that
  will use it are named and not described.
