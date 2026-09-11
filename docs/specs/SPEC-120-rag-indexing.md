# SPEC-120 — Indexing

| | |
|---|---|
| Status | Draft |
| Version | 0.12.0 |
| Owner | Indexing |
| Last updated | 2026-09-11 |

## Purpose

Enumerates files, splits their text into chunks, and keeps the index in step with the folder
as it changes.

## Scope

**In scope**

- File enumeration and filtering.
- Tokenization and chunking.
- Deciding what has changed since the last pass, and acting on it.
- When a pass runs, what triggers the next one, and what retrieval is told while one has not
  finished.

**Out of scope**

- The embedding implementation ([SPEC-160](SPEC-160-embedding-module.md)).
- Storage schema ([SPEC-130](SPEC-130-persistence.md)).
- Retrieval ([SPEC-110](SPEC-110-rag-retrieval.md)).

## Enumeration

- An extension allow-list, not a binary sniff. The set is the declared scope; something
  unreadable that happens to look like text costs an embedding and pollutes the index.
- Files above a configured size are skipped.
- Build, VCS and metadata directories are not descended into.
- **The scan fails loudly rather than returning an empty result.** See the sharp edge below.

## Chunking

Overlapping windows of a fixed token count, with a configured overlap. Chunk identity is
content-addressed, derived from the file, the window index and the content hash.

**A trailing window whose tokens the previous chunk already covers in full is dropped rather
than emitted.** It is a suffix of its predecessor, produced only because the step happened to
land there — seven tokens at size 4 and overlap 2 would otherwise end with a chunk holding just
the seventh token. Emitting it costs an embedding and lets the same text come back twice in one
result set. Only the final window can be redundant this way, because windows advance
monotonically, and dropping it never drops a token.

**A token is a range into the file's text, not a string cut out of it.** Chunk text is assembled
from those ranges. Tokens exist only to be joined back into chunk text, out of the very string
they came from, so materialising each one costs an object per token — tens of millions of them
over a corpus — for no information that the offsets do not already carry.

**Chunk text is single-space-joined, and that normalisation is fixed.** It is not a verbatim
slice of the source: runs of whitespace, tabs and line breaks between tokens all become one
space. The joined text feeds the chunk hash and so the content-addressed chunk id, which means
joining differently renames every chunk in every folder already indexed — and the next pass would
then delete the stored chunks while the unchanged-file check declined to re-embed their
replacements, emptying the index without failing.

### The pipeline streams text, and must keep doing so

The scanner yields files **lazily**, reading each one's text only as it is pulled. The pipeline
chunks a file, embeds it if it changed, and drops its text before pulling the next.

**Nothing that carries text may be accumulated across files.** A file's text exists to be chunked
and a chunk's text to be embedded; neither is ever stored — `chunk_manifest` holds ids, offsets
and hashes and no content at all. What accumulates for the write is metadata and vectors.

This is enforced by types rather than by discipline: `ScannedFile` and `ChunkMetadata` are the
text-free halves of `ScannedTextFile` and `TextChunk`, and the repository's write method takes
only those. A signature that demanded text the write does not store would oblige the pipeline to
hold the corpus purely to satisfy it, and there would be nothing but a comment to stop it.

**The one exception is a corpus-fitted vectorizer with no fit yet.** It cannot embed anything
until it has seen every chunk, so that path alone keeps chunk text alive until the fit is
computed. It is bounded to the first index under a new model version, since an existing fit is
loaded and reused rather than recomputed.

The write remains a **single transaction**. Streaming changed what is held in memory, not the
atomicity of what is stored.

A pass **ends with a truncating WAL checkpoint**, once, after that transaction commits. A
whole-folder pass is the largest single write the system makes, and without one the folder kept a
log about as large as the database beside it until some later writer reclaimed it — measured, and
the policy governing when a checkpoint may run, in [SPEC-130](SPEC-130-persistence.md).

## Delta handling

A pass classifies every file into one of four outcomes.

| Outcome | Condition | Action |
|---|---|---|
| **Create** | No manifest row for the file | Chunk and embed |
| **Update** | Content hash differs from the manifest | Chunk and re-embed; superseded chunks removed |
| **Skip** | Hash matches **and** the active model has already embedded it | Chunk, do not embed |
| **Delete** | Manifest row with no file behind it | Remove the row; chunks and vectors cascade |

### Skip needs two conditions, not one

**Content equality alone is not sufficient.** After switching embedding implementation, every
file is unchanged — yet none of them has a vector in the new model's space. Skipping on the
hash alone would leave the new model with a silently empty index and no error anywhere.

So a file is skipped only when its hash matches **and** the active `model_version_id` already
has vectors for it. The vector-existence check is per model version, which is what makes the
model-switch case work.

### What is skipped, and what is not

**Only embedding is skipped.** Chunking runs for every scanned file, every pass:

- the scanner has already read the content, so the expensive part is paid;
- chunking is cheap next to an embedding call;
- a corpus-fitted vectorizer needs the whole chunk set regardless of what changed.

Embedding is the expensive stage and the only one avoided, which is what makes restarting over
an unchanged folder cheap.

### Superseded chunks

A content edit produces a new content-addressed chunk id for the same `(file_id, chunk_index)`
slot, and a file that shrank leaves trailing chunks with nothing to overwrite them. Both are
removed before the new chunks are written; their vectors cascade away. That is what keeps every
stored vector bound to the content it was computed from, under every model version.

### Fit reuse

An existing fit artifact is **reused, not recomputed**. Refitting changes the projection and
would invalidate every vector already stored under that model version, so it stays a deliberate
operation under a new `model_version_id` ([SPEC-161](SPEC-161-embedding-programmable.md)) rather
than something an ordinary edit triggers.

**Accepted cost:** incremental updates embed against the corpus as it was when the fit was
taken, so vocabulary and inverse document frequencies drift as the folder changes. **Drift
detection is not implemented.**


## Execution model

Indexing runs in a background service. The web host starts immediately and does not wait for a
pass to finish, because that wait costs whatever the analyzed folder costs — a number that has
nothing to do with the application and no upper bound the application controls.

**The database bootstrap did not move.** It stays synchronous and still completes before any
request is handled ([SPEC-130](SPEC-130-persistence.md)). Only indexing went to the background.

### Readiness

Moving indexing off the startup path gives up a guarantee that used to be free: that a host
which is answering has an index behind it. `IIndexState` is what replaces it, exposing
`Building` / `Ready` / `Failed`.

**Retrieval refuses while the index is building.** It does not answer from what has been stored
so far. A partial index does not fail a query — it returns hits, and those hits are
indistinguishable from the ones a complete index returns for a query it has nothing good for.
Nothing in the scores carries "ask again shortly", so refusing is the only response that keeps
the two apart, and it lets a caller wait or degrade on purpose instead of acting on a result
that is quietly wrong.

A failed build is reported as a failure, carrying its cause, rather than as "still building".
One resolves by waiting and the other does not.

**The failure is contained.** A background service that lets its exception escape takes the host
down with it, which would turn an unreadable folder into a stopped application rather than one
degraded feature.

**When indexing is disabled**, the state is marked ready at startup. Nothing is going to build an
index, so a query must not sit waiting for one; ready over an empty store returns no hits, which
is the honest answer. `IndexingDisabledService` is what does it: whether indexing runs at all is
decided when the hosted-service factory executes, which is after the configuration is final
([SPEC-100](SPEC-100-conversation-orchestration.md)).


### Keeping up with the folder

After the first pass, a change feed drives the rest. `IFileChangeFeed` emits
`FolderChangeSignal(Reason)`.

**The signal is deliberately coarse.** It names no file, and a consumer cannot learn from it what
was edited. A pass rescans and diffs by content hash regardless, so per-file detail would be
gathered, carried and then ignored — while making the feed answerable for being complete and
correct about a set of events that cannot be obtained reliably in the first place.

That is what makes the feed survivable on top of a filesystem watcher, which is unreliable in two
specific ways:

- its internal buffer overflows under a burst and the events in it are lost, so an overflow is
  reported as "assume everything changed" rather than as an attempt to reconstruct what went
  missing;
- an editor saving atomically writes a temporary file and renames it over the original, which
  arrives as delete-then-create rather than as a change — so a feed describing the edit would
  describe the wrong thing, while a feed that only says "look again" is right either way.

**A burst collapses into one pass.** Signals are debounced, and the pending signal is held in a
one-slot channel that drops writes when full: ten edits cost one pass, not ten identical ones.

**A periodic rescan backstops the watcher**, for anything it never reported at all.

**Passes are serialized.** The next signal is not read until the current pass returns, so two
passes cannot write over each other however quickly the folder is being edited.

### The watcher must ignore the metadata folder

The folder database lives inside the analyzed folder, so every pass writes files the watcher can
see. Unfiltered, each pass would trigger the next one and the folder would index for as long as
the process ran. Build and VCS directories (`bin`, `obj`, `.git`, `.vs`, `node_modules`) are
ignored as well.

The configured metadata folder name is passed to the feed rather than assumed, so changing it
does not quietly reopen the loop.

### Configuration

- `LlmAgent:Indexing:WatchEnabled` (default `true`) — watch the folder and re-index on change.
- `LlmAgent:Indexing:DebounceMilliseconds` (default `750`) — quiet period after a file event.
- `LlmAgent:Indexing:ReconciliationIntervalSeconds` (default `300`) — periodic full rescan. Zero
  disables it.

## Sharp edge — deletion is inferred from the scan

The scanned file list is treated as the authoritative current state of the folder. **A scan that
silently returned nothing would clear the index.**

This is currently sound because the scanner throws on a bad path rather than returning an empty
result, and that property is therefore load-bearing rather than defensive. Anything that later
makes the scan partial — a permissions error swallowed per directory, a cancelled walk returning
what it had — breaks this without any test necessarily noticing.

## Non-functional requirements

- **Reliability** — re-indexing an unchanged folder writes no new vectors and loses nothing.
- **Performance** — **embedding** cost scales with what changed rather than with corpus size.
  The cost of a *pass* is dominated by working out what changed, which is why that query shape
  is load-bearing: a pass with nothing to do took 104 s before the manifest read was
  materialised rather than asked per file, and 3.4 s after. Measurements in
  [SPEC-131](SPEC-131-database-options-analysis.md).
- **Memory** — measured with `CorpusBenchmark` over generated corpora of 4,000 and 12,000 files
  (the latter 208 MB of text). Two separate properties, and conflating them misreads both.
  *Allocation*: holding tokens as ranges rather than strings cut the tokenise-and-chunk stage from
  **4,281 MB to 906 MB** at 12,000 files (1,427 → 302 at 4,000). That did **not** move peak working
  set — 1,061 MB against 1,067 — because it was garbage the collector was already absorbing.
  *Retention*: streaming the text cut peak working set from **794 MB to 289 MB** at 12,000 files
  and **413 MB to 153 MB** at 4,000, while leaving allocation untouched (2,130 MB against 2,135).
  So allocation and footprint are moved by different changes, and only the second one sets the
  ceiling on how large a folder can be indexed.
- **Operability** — a pass reports scanned, indexed, unchanged and deleted counts, so "nothing
  happened" and "nothing needed to happen" are distinguishable.

## Rollout

The index is derived state: deleting the folder database and re-running rebuilds it. Two
properties make that a fallback rather than the plan.

**A folder indexed before trailing windows were dropped self-heals on the next pass.** Chunking
runs for every scanned file, including unchanged ones, so the now-absent chunk is reconciled out
of `chunk_manifest` and its vector cascades away. The surviving chunks keep their `chunk_id`s and
their vectors, so nothing is re-embedded — the file does not even have to have changed. This is
covered by a test that plants exactly such a stale chunk, rather than left as a claim: a chunk
that survived would go on returning duplicate text from retrieval indefinitely, on precisely the
databases nobody thinks to rebuild.

**Reverting the pipeline restores full re-embedding**, since change detection is transparent to
the schema.

## Test strategy

What is actually pinned, so that a claim here can be checked against a test rather than taken on
trust.

- **Delta correctness** — re-indexing an unchanged folder embeds nothing; only a changed file is
  re-embedded; a deleted file's chunks and vectors go; switching model version re-embeds
  everything despite unchanged content.
- **Idempotency** — re-indexing changed content supersedes rather than collides, and a file that
  shrank leaves no orphaned chunks.
- **Fit reuse** — editing a file does not silently refit an existing corpus-fitted model.
- **Readiness** — retrieval refuses while the first index builds and after a failed build; a
  failed refresh leaves a ready index serving, and unmarked.
- **Background service** — the initial pass moves `Building` to `Ready`; a change signal runs
  another pass; a failed pass does not fault the host.
- **Change feed** — an edit produces a signal; writes inside the metadata folder produce none, for
  the default name *and* a configured one; build output is ignored; a burst collapses into one
  signal; the periodic tick fires with no file event, and a zero interval disables it.
- **The watcher end to end** — a real filesystem event reaching a real re-index, asserting that
  indexing *stops*. Asserting on file counts alone does not catch a self-triggering loop: it
  re-indexes the same files and leaves the counts and the status looking correct.
- **Scanner filters** — a file over `MaxTextFileSizeBytes` is excluded and one exactly on the
  limit is kept; `bin`, `obj`, `.git`, `.vs`, `node_modules` and the metadata folder are not
  scanned, nested or otherwise; an extension outside the allowlist is excluded; empty and
  whitespace-only files are skipped; the file id tracks the path while the hash tracks the
  content; an unreadable root throws rather than reporting an empty folder.
- **Chunk window arithmetic** — consecutive chunks overlap by exactly the configured count; a
  trailing window already covered by the previous chunk is dropped, and dropping it loses no
  token across a sweep of token counts against six size/overlap combinations; zero
  overlap partitions without repeating; the final window is truncated rather than padded;
  identical content at the same index yields the same `chunk_id` and different files do not; a
  chunk size of zero or an overlap outside `[0, size)` is rejected.
- **Configuration** — defaults match this document, and the analyzed folder falls back to the
  working directory and always resolves to an absolute path.

- **Scale (measured, not asserted)** — `CorpusBenchmark` reports stage timings over a generated
  corpus large enough for query shape to matter. It is opt-in and asserts nothing: a benchmark
  that fails a build on a timing threshold turns machine variance into a red suite. The numbers
  it produced are in [SPEC-131](SPEC-131-database-options-analysis.md).

## Open questions

- Whether fit drift should be detected, and what the signal would be.
- Whether a partial scan should be distinguishable from a complete one, so deletion can be made
  conditional on the scan being known-complete.
- Every signal triggers a rescan of the whole folder. That is cheap next to embedding, since
  unchanged files are skipped, but it is O(folder) per edit. Should the signal carry enough detail
  to narrow the rescan once a corpus is large enough for that to matter?
- Should retrieval say so while a refresh is in flight, or is silent staleness acceptable? Today a
  query during a refresh is answered from the previous pass with nothing marking it as such.

## References

- [SPEC-130 — Persistence](SPEC-130-persistence.md)
- [SPEC-160 — Embedding module](SPEC-160-embedding-module.md)
- [Reduction pipeline blueprint](../diagrams/reduction-pipeline-library-implementation-blueprint.md)
