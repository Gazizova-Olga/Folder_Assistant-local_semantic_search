# Analyzer warnings — the gate, and what is suppressed under it

`SonarAnalyzer.CSharp` runs as a global analyzer on every build, and since 2026-09-10 the build
**fails on any warning**: `TreatWarningsAsErrors` in [Directory.Build.props](../Directory.Build.props).

Before that, this file was the gate — a documented count that a human had to remember to compare a
build against. That arrangement rots: warnings accumulate, nobody reads them, and a real defect
hides in the noise.

It already happened here. Two of the warnings this file was written to triage were not noise at
all — see "Fixed, for the record" below. One was a mis-bound `Split` overload in the watcher's
metadata-folder guard, and **no test caught it**: the suite was green with the bug in place, before
and after.

**The baseline is now zero, and it is enforced rather than documented.** A new warning is a build
error at the moment it is introduced, by whoever introduced it, instead of a line in a file someone
may not read.

- **Baseline: 0 warnings, 0 errors.** Verified 2026-09-10, .NET 10 SDK.
- Nothing to recheck by hand any more — `dotnet build` is the check. If it succeeds, the baseline
  holds.

## What this file is for now

Two things, and neither is a count:

1. **The justification for every suppression in the tree.** Four warnings are deliberate. They are
   carried as targeted `[SuppressMessage]` attributes with written reasons, on the members they
   describe. This file lists them so they can be reviewed together, but the attributes are the
   source of truth — a reader meeting one in the code gets the reasoning without leaving the file.
2. **The record of what was cleared and why**, below, including the two defects that were hiding in
   the noise.

**How to add one.** A new suppression needs a justification that says *why the code is right*, not
why the rule is annoying. If that sentence cannot be written, the warning is telling the truth and
the code should change. Never silence a rule repo-wide in `.editorconfig` to get a build green —
that removes the warning everywhere, including where it would have been correct next time.

## Deliberate — carried as [SuppressMessage], with these justifications (4)

These fire on code that is correct as written; changing it to satisfy the analyzer would break it.
Each is carried as a targeted attribute on the member itself — the gate stays on, and the exception
is visible where someone would meet it. Sites are named by member, not line, because line numbers
drift and this table went stale that way once already.

| Rule | Site | Why it stays |
| --- | --- | --- |
| `S1215` (`GC.Collect`) | `PersistenceConcurrencyTests.Concurrent_Bootstraps_Of_The_Same_Folder_Never_Fault` | The collect is what makes the test detect anything. The fault it guards against is a database handle finalized while another connection is inside `sqlite3_prepare_v2`, so collections have to land *during* the concurrent work. Removing it leaves the same 60 iterations passing even with the shared cache restored — the test silently stops guarding. |
| `xUnit1031` (blocking wait) | the same test | It deliberately drives eight threads at one bootstrap and joins them; that is the scenario under test, not an accident. |
| `S1144` (unused constructor) | `Program.StartupDependencies` constructor | False positive. `StartupDependencies`' constructor is invoked by the container, and **that invocation is the mechanism** ordering the database bootstrap before the server listens (`SPEC-130`) and the roster's validation before a turn can run (`SPEC-100`). It looks unused precisely because nothing calls it explicitly. |
| `S2699` (test without assertions) | `CorpusBenchmark.Measure_The_Baseline` | It is a measuring instrument, not a test, and it says so. It asserts nothing on purpose: a benchmark that fails a build on a timing threshold turns machine variance into a red suite. It lives under `[Fact]` because that is the runner already present, and it returns immediately unless an environment variable asks for it. |

## Cleared to reach zero, 2026-09-10

Four warnings stood between the tree and the gate. None was silenced.

| Rule | Site | How it was cleared |
| --- | --- | --- |
| `S3878` — array created for a `params` parameter | `FolderIndexingPipelineTests.cs(54)` | Genuinely redundant. `BeEquivalentTo(["real.md"])` became `BeEquivalentTo("real.md")`; the array was doing nothing. |
| `S2325` — could be static | `LocalTextFileScanner.Enumerate` | **Suppressed** at the time, kept an instance method by design — see below. **Suppression removed 2026-09-16:** the scanner now holds the extraction registry and `Enumerate` walks with it, so the method reads instance data and the rule no longer fires. |
| `S2325` — could be static | `SimpleTokenizer.Tokenize` | **Suppressed**, same reason. |
| `S2325` — could be static | `TextChunker.Chunk` | **Suppressed**, same reason. |

**Why the `S2325` sites are suppressed rather than made static** (two remain:
`SimpleTokenizer.Tokenize` and `TextChunker.Chunk`). Both types are used as instantiable
collaborators: each is a `new()` field on `FolderIndexingPipeline` and is constructed directly at
test call sites (`new TextChunker()`). Making the methods static would change how every caller
reaches them, and would buy nothing at runtime. The analyzer is reporting a fact about the method
body; it is not reporting a problem with the design.

`S3267` on `FileChangeFeed.ShouldIgnore` was cleared one commit earlier by fixing the code — the
detail is at the end of this file.

## Fixed, for the record

Two of the warnings in the previous 10 were real defects. Both were introduced by the background
indexing work and both are fixed in the commit that adds this file.

- **`S3220` in `FileChangeFeed.cs`** — `relative.Split(Path.DirectorySeparatorChar,
  Path.AltDirectorySeparatorChar)` looks like the `params Char[]` overload but bound to
  `Split(Char, Int32)`, because `Char` converts implicitly to `Int32` and the non-expanded form
  wins overload resolution. The alt separator silently became a **count of 47**, so the path was
  never split on `/`. This is the guard that stops the indexer's own database writes from waking
  the watcher and re-indexing forever. Fixed by splitting on an explicit `Char[]` field; the
  field carries a comment saying it must stay an array, because inlining the two chars is
  exactly how the bug got in.

  **This fix is not covered by a test, and that is stated rather than papered over.** Reverting
  it leaves all 154 tests passing — measured, twice. On Windows `Path.GetRelativePath` returns
  backslash separators, so the mis-bound overload still splits them; the bug shows only on a
  path using `/`, or one more than 47 segments deep. The analyzer is the detector here, which is
  the whole argument for keeping this baseline honest.

- **`S3626` in `FolderIndexingService.cs`** — a redundant `if (isInitial) { return; }` at the
  end of a `catch`, under a comment claiming it kept a failed refresh from tearing down a live
  index. It did not: it returned to where falling off the end would. The invariant actually
  lives in `IndexState.MarkFailed`, which refuses to leave `Ready`. Removed the branch and the
  now-unused parameter, and repointed the comment at the code that does the work. No test can
  catch this one either — the branch was a no-op, so removing it changes nothing observable.

A third defect went with them, reported by no analyzer at all: **`MarkFailed` published the
error before checking the status**, so a `Ready` index could carry a stale exception — and
`Error` is the reason an index is unusable, not a log of the last thing that went wrong. It now
records nothing when it declines to leave `Ready`. That one *is* covered:
`A_Failed_Refresh_Leaves_A_Ready_Index_Unblemished` fails if the check is reverted, measured.

## Cleared, 2026-09-10 — `S3267` in `FileChangeFeed.ShouldIgnore`

This file previously recorded that warning as *"arguably wrong: the nested loop returns on the
first match, and a `Where` would express the same short-circuit less clearly while allocating"*.
That defence was half right and it was defending the wrong shape.

The short-circuit argument holds against `Where`. It does not hold against the actual problem,
which was a **nested** loop: for every path segment, a linear scan of the ignored-directory list.
The list is a set lookup by nature, and `LocalTextFileScanner` had already been using a
case-insensitive `HashSet` for the identical job. Making the two agree removes the warning
honestly rather than suppressing it, and `Any` over a set keeps the short-circuit the old code had.

`ShouldIgnore` is the method a real defect hid in once — the `Split` overload mis-binding recorded
below — so `FileChangeFeedTests` was run against this change specifically: 8 passed, including the
metadata-folder and build-output guards this method exists to provide.

**Not recorded here: a second `S3267`.** `SimpleTokenizer.Tokenize` loops over
`Regex.EnumerateMatches`, whose enumerator is a `ref struct` that LINQ cannot be applied to at all
— the transformation the rule wants would allocate a `Match` per token and revert the `SPEC-120`
work. This build does not raise the warning there, so there is nothing to file as deliberate, and
inventing an entry for a warning that does not fire would make this document describe a build that
does not exist.
