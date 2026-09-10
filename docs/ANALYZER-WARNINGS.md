# Analyzer warning baseline

`SonarAnalyzer.CSharp` runs as a global analyzer on every build, and the build does **not** fail
on warnings. That combination rots: warnings accumulate, nobody reads them, and a real defect
hides in the noise.

It already happened here. Two of the warnings this file was written to triage were not noise at
all — see "Fixed, for the record" below. One of them was a mis-bound `Split` overload in the
watcher's metadata-folder guard, and **no test caught it**: the suite was green with the bug in
place, before and after.

This file is the baseline. **The count is the contract**: a build reporting more than this has
introduced something new, and it gets triaged rather than added to the pile.

- **Baseline: 8 warnings.** Verified 2026-09-10, .NET 10 SDK.
- Recheck with:

  ```bash
  dotnet build --no-incremental -v q --nologo 2>&1 \
    | grep -oE "[A-Za-z0-9._]+\.cs\([0-9]+,[0-9]+\): warning [A-Za-z0-9]+" | sort -u
  ```

  The raw log double-counts — each project is reported once in the body and once in the summary
  — so `sort -u` is what makes the number comparable. An *incremental* build reports fewer
  still, because a project that did not recompile reports nothing. Know which build you are
  reading before treating a number as a regression.

## Deliberate — do not "fix" these (4)

These fire on code that is correct as written. Changing the code to satisfy the analyzer would
break it. They are the reason this file exists rather than a blanket `TreatWarningsAsErrors`.

| Rule | Site | Why it stays |
| --- | --- | --- |
| `S1215` (`GC.Collect`) | `PersistenceConcurrencyTests.cs(54)` | The collect is what makes the test detect anything. The fault it guards against is a database handle finalized while another connection is inside `sqlite3_prepare_v2`, so collections have to land *during* the concurrent work. Removing it leaves the same 60 iterations passing even with the shared cache restored — the test silently stops guarding. |
| `xUnit1031` (blocking wait) | `PersistenceConcurrencyTests.cs(47)` | Same test. It deliberately drives eight threads at one bootstrap and joins them; that is the scenario under test, not an accident. |
| `S1144` (unused constructor) | `Program.cs(140)` | False positive. `StartupDependencies`' constructor is invoked by the container, and **that invocation is the mechanism** ordering the database bootstrap before the server listens (`SPEC-130`). It looks unused precisely because nothing calls it explicitly. |
| `S2699` (test without assertions) | `CorpusBenchmark.cs(34)` | It is a measuring instrument, not a test, and it says so. It asserts nothing on purpose: a benchmark that fails a build on a timing threshold turns machine variance into a red suite. It lives under `[Fact]` because that is the runner already present, and it returns immediately unless an environment variable asks for it. |

## Noise — worth clearing, no behaviour at stake (4)

Mechanical. Clearing these is what restores signal.

| Rule | Sites |
| --- | --- |
| `S2325` — could be static | `LocalTextFileScanner.cs(43)`, `SimpleChunking.cs(63, 93)` |
| `S3878` — redundant array creation | `FolderIndexingPipelineTests.cs(54)` |

## Intended end state

Clear the 4, attach targeted `[SuppressMessage]` attributes carrying the justifications above to
the 4 deliberate ones, then set `TreatWarningsAsErrors` in a `Directory.Build.props`. The
baseline becomes zero and the build enforces it, which is strictly better than a document
someone has to remember to read. Until then, this file is the thing to check a build against.

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
