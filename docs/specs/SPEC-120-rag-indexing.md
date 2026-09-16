# SPEC-120 — Indexing

| | |
|---|---|
| Status | Draft |
| Version | 0.18.0 |
| Owner | Indexing |
| Last updated | 2026-09-16 |

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
  unreadable that happens to look like text costs an embedding and pollutes the index. The list is
  the text extraction registry's (below), not the scanner's; the scanner asks it per file, for
  whether the file is read at all and for the text once it is.
- Files above a configured size are skipped.
- Build, VCS and metadata directories are not descended into.
- The content hash the scanner takes is SHA-256 over the file's **bytes**, before decoding. It is
  compared against the hash the front end recorded for the same file
  ([SPEC-121](SPEC-121-file-indexing-front-end.md)), and the two must agree byte for byte: a file
  whose scanned hash differs from its record is deferred to the front end, so hashing differently
  would defer the whole corpus — not wrong, but a pass that embeds nothing. A hash of the decoded
  text would differ wherever a byte-order mark or an unreadable sequence was dropped. The file id
  stays a hash of the path, the same one the front end derives, because the pass writes chunks
  under it against the row the front end recorded.
- **The scan reads and chunks; it records nothing about a file and deletes nothing.** What the
  folder contains is recorded by the front end's comparison, which the pass runs first (Delta
  handling, below). The scan still fails loudly on a root it cannot read rather than returning
  an empty result, but nothing hangs on that any more — see the sharp edge below for where it went.

## Text extraction

One registry, `TextExtractorRegistry`, is the single source of which files this system reads and how
each is turned into text. It holds extractors, each of which claims a set of extensions, says
whether its formats **scan as raw lines** — whether the bytes on disk are the text — and turns a
file's bytes into its text. Everything that walks the folder asks that one instance: the
whole-folder pass through the scanner, the per-file delivery through the bridge, the watcher's
filter through the extension list the host hands it
([SPEC-121](SPEC-121-file-indexing-front-end.md)), and the text search
([SPEC-101](SPEC-101-file-tools.md)). Two lists would drift, and the drift would be silent: a file
indexed by one path and ignored by another looks exactly like a file that was never saved.

- **Bytes in, text out.** Both writers of a file's record hash the file's bytes before decoding
  (above), so the bytes are already in hand; an extractor that took a path would open the file a
  second time under a writer the first open just survived.
- **An extension claimed by two extractors is refused at construction.** Letting the later one win
  would make which text a file yields depend on registration order, which nothing reading the index
  could tell from the outside.
- **One extractor exists: plain text**, decoding as `File.ReadAllText` decodes — UTF-8 unless a
  byte-order mark says otherwise, the mark left out — over the extensions the scanner used to list
  for itself. It scans as raw lines. Extractors for documents in containers (`.docx`, `.pdf`) are in
  scope only once a rebuilt snippet is verified against its chunk hash
  ([SPEC-110](SPEC-110-rag-retrieval.md)): without that check, a passage rebuilt from the file's
  bytes at a token window taken over extracted text is a wrong passage under a real path and a real
  score.
- **Raw-line scanning is a property of the format, asked separately from "read at all".** The text
  search reads only formats that scan as raw lines; a container format the index reads would
  otherwise be scanned through its packaging. An extension nothing reads answers false to both
  questions.

The application registers one instance in its composition root and hands it to every consumer. The
scanner and the pipeline take it at construction, with the application's default where a test builds
them bare.

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

**The pass records nothing about a file and deletes nothing.** Before it reads a single file it
runs the front end's own comparison ([SPEC-121](SPEC-121-file-indexing-front-end.md),
Reconciliation) over the folder, into the store that owns `file_manifest`. Every row — hash,
size, creation time — and every queued delivery comes from that one classifier, so a row written
for the pass is indistinguishable from one the front end writes later, and there is exactly one
derivation of a file's record. The pass then classifies each file it reads against that record:

| Outcome | Condition | Action |
|---|---|---|
| **Embed** | Recorded at the content read, and either the comparison just recorded that content as added or modified, or the active model has no vectors for it | Chunk and embed; superseded chunks removed; marked delivered |
| **Unchanged** | Recorded at the content read, already embedded by the active model, and not recorded as changed by this comparison | Chunk, rewrite the chunk rows, do not embed |
| **Deferred** | Not recorded, or recorded at other content than was read | Nothing; the delivery the front end holds for it does the work |
| **Removed** | Recorded, no file behind it | Recorded and queued by the comparison; delivered by the dispatcher, which clears vectors, then the row, whose cascade takes the chunks |

A deferred file is in flux — locked when the comparison hashed it, or written between the
comparison and the read — and the pass has nothing it can honestly write for it: chunks under no
row, or a delivery mark for content the row does not describe. Either way the front end has, or
will have, a delivery for it.

**The pass marks what it embedded as delivered**, through the store's conditional mark
([SPEC-121](SPEC-121-file-indexing-front-end.md), the outbox) and only after its chunks and vectors
have committed, so the deliveries the comparison queued find their work already done and skip: a
cold start costs one embed per file, not two. The order is the safe one. Marked before the vectors,
a crash between would leave a file believed indexed that is not; the other way round costs one
duplicate delivery.

### Skip needs three conditions, not one

**Content equality alone is not sufficient, in two directions.** After switching embedding
implementation, every file is unchanged — yet none of them has a vector in the new model's space,
and skipping on the hash alone would leave the new model with a silently empty index and no error
anywhere. And a file the comparison just recorded at a new hash has vectors for the content
*before* the edit, so the vector check alone would skip exactly the file that changed.

So a file is skipped only when its hash matches the record **and** the active `model_version_id`
already has vectors for it **and** the comparison at the start of the pass did not record it as
added or modified. The vector-existence check is per model version, which is what makes the
model-switch case work; the comparison's conclusion is what makes the edit case work.

### What is skipped, and what is not

**Only embedding is skipped.** Chunking runs for every scanned file, every pass:

- the scanner has already read the content, so the expensive part is paid;
- chunking is cheap next to an embedding call;
- a corpus-fitted vectorizer needs the whole chunk set regardless of what changed.

An unchanged file's chunk rows are rewritten as well. The write is idempotent for
content-addressed ids, and it is what reconciles away a chunk row the current chunker would not
produce — a stale trailing window from an older chunker, say — without an embed (Superseded
chunks, below).

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

**A failed build is not permanent.** What fails a first index is usually outside this process and
usually temporary — an embedding backend that has not finished starting, a model still being
pulled. Left at `Failed`, every search refuses for the life of the process and the remedy is
restarting an application that would have recovered on its own, so the first pass is retried on an
interval (`FailedIndexRetryIntervalSeconds`, 30 s) until it succeeds or the host stops. Three parts
of that are the rule rather than the implementation:

- **A probe that has already passed is not repeated.** A pass can fail on either side of it, and
  re-asking a backend that answered costs a round trip for an answer that will not have changed,
  where the pass that failed after it is the part worth trying again.
- **Zero disables retrying**, which is the opt-out for a deployment that would rather a failure
  stood — and it has to be a real branch rather than a very short interval, because a zero wait
  would make the retry a busy loop.
- **Stopping does not wait out an interval.** The interval says how long to keep trying to recover
  and nothing about how long a host may take to shut down.

**The failure is contained.** A background service that lets its exception escape takes the host
down with it, which would turn an unreadable folder into a stopped application rather than one
degraded feature.

**When indexing is disabled**, the state is marked ready at startup. Nothing is going to build an
index, so a query must not sit waiting for one; ready over an empty store returns no hits, which
is the honest answer. `IndexingDisabledService` is what does it: whether indexing runs at all is
decided when the hosted-service factory executes, which is after the configuration is final
([SPEC-100](SPEC-100-conversation-orchestration.md)).


### Keeping up with the folder

After the first pass, the file-indexing front end keeps the index in step with the folder
([SPEC-121](SPEC-121-file-indexing-front-end.md)): a debounced watcher, a periodic comparison as
the safety net for what the watcher never reports, a durable outbox, and a dispatcher that delivers
one changed file at a time to the embedding side. Nothing described here runs a second whole-folder
pass: the corpus pipeline runs **once**, at start.

**The first pass stays, and runs first.** A corpus-fitted embedder has to see the entire corpus
before it can embed anything, and a cold folder is cheapest to embed in one batched pass. The front
end starts only after that pass has succeeded, and the pass never runs again while the front end
runs, because the pass embeds against a snapshot of the record and the loops move the record. There
is no second writer of the file table to keep apart: the pass records through the front end's own
comparison and writes only chunks, vectors and the delivery mark (Delta handling, above;
[SPEC-121](SPEC-121-file-indexing-front-end.md), one writer of the file table).

**Readiness takes all three.** An attempt is the probe, the pass, and the start of the front end —
which compares the folder against the index once itself before its loops run, cheaply, since the
pass's own comparison already recorded every file it will find. That is the same folder hashed
twice in one start, and it is the accepted cost of the front end owning its own start rather than
trusting a caller to have compared first. The index is `Ready` only after all three. A front end
that could not start fails the attempt and is retried like anything else: a ready index that had
quietly stopped following the folder would be worse than one still building, because nothing would
report it.

What the front end promises — a burst of edits costing one delivery, a change reported by a writer
inside this process, a hold for a multi-step edit, the metadata folder never indexing itself — is
specified there rather than here.

### Configuration

- `FolderAssistant:Indexing:DebounceMilliseconds` (default `750`) — how long a changed file must
  go untouched before its change is processed.
- `FolderAssistant:Indexing:ReconciliationIntervalSeconds` (default `300`) — how often the front
  end compares the whole folder against the index. Zero disables it; the comparison at start still
  runs.

## The embed window

Chunks are gathered across files and embedded a fixed window at a time
(`Indexing.EmbeddingBatchSizeChunks`, default 64) rather than a file at a time.

**It is a memory bound, and that phrasing is exact rather than modest.** Each chunk is embedded
independently of the others sharing its call, so where the window cuts changes nothing that gets stored —
which is precisely what frees it to cut at a fixed count instead of on a file boundary. A call per file
held one file's entire chunk text, so a single large file had no bound at all; the window caps what is
alive at one window's worth, however large the folder or the file.

**What it does not buy is speed, and that non-reproduction is the point of recording it.** The obvious
expectation is that turning twenty calls into one saves twenty round trips. Measured against the local
server on this machine it does not. First index of the same corpus, windows of 1, 8 and 64, three passes
each, alternating so that drift lands on all of them:

| Window | Median | Passes |
|---:|---:|---|
| 1 (a call per chunk) | 10,959 ms | 10,458 · 11,049 · 10,959 |
| 8 | 10,527 ms | 10,313 · 10,536 · 10,527 |
| 64 | 10,519 ms | 10,940 · 10,519 · 10,518 |

Report: [docs/benchmarks/embed-window.md](../benchmarks/embed-window.md).

**The measurement is powered for the claim it refutes and not for the one it makes.** A halving would have
been unmissable at this sample size — about 5,500 ms against 11,000. The roughly 4% it does show sits
inside the spread of the passes behind each median, and is not a result; the passes for window 1 and
window 64 overlap outright.

The reading that fits: the per-call cost here is the model's own inference, around 400 ms per chunk on
this CPU, and the server works through an array rather than embedding it at once. Batching then saves HTTP
overhead against a cost that is not HTTP. A backend that embedded a batch in parallel would be a different
measurement, and this number must not be carried across to one.

## Sharp edge — deletion is inferred from the comparison

The comparison at the start of a pass, and the periodic one after it, treat their walk as the
authoritative current state of the folder. **A walk that silently returned nothing would record
every file as removed** and queue the removal of the whole index — delivered, that clears every
vector.

The scan itself no longer deletes anything, so this edge moved with the deletion to the front end's
reconciler ([SPEC-121](SPEC-121-file-indexing-front-end.md), Reconciliation), where two properties
hold it: a file that could not be hashed is left out of the picture rather than treated as gone, and
a root that cannot be enumerated at all throws rather than returning empty. One property does
*not* hold it and is worth knowing: the walk skips a subtree it cannot enter without counting it,
so a folder that disappeared mid-walk and a folder that merely denied access look the same, and
the recorded files under either are classified as removed until the next comparison finds them
again. Anything that later makes the walk partial in a new way — a cancelled walk returning what
it had — breaks this without any test necessarily noticing.

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
  ceiling on how large a folder can be indexed. The embed window (above) bounds the last unbounded
  thing on that path: what one embed call holds. It buys no measured speed here, which is why it is
  described as a bound rather than an optimisation.
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
- **Background service** — the initial pass moves `Building` to `Ready`; the front end starts once
  the first pass has succeeded and not before; a front end that cannot start fails the attempt and
  is tried again, pass included; stopping the service stops it; a failed pass does not fault the
  host.
- **End to end** — a whole-folder pass, then a file written to the folder is embedded and a deleted
  one's vectors go, with nothing in the test calling any stage; and the outbox goes quiet once the
  folder does, with no queued operation ever naming the metadata folder. That last assertion is the
  one that catches the index feeding itself, which file counts alone do not: a self-triggering loop
  re-indexes the same files and leaves every count looking correct. The same path is driven once
  more through the real composition root, since a registration that resolved the wrong thing would
  boot and serve while the folder quietly stopped being followed.
- **Scanner filters** — a file over `MaxTextFileSizeBytes` is excluded and one exactly on the
  limit is kept; `bin`, `obj`, `.git`, `.vs`, `node_modules` and the metadata folder are not
  scanned, nested or otherwise; an extension the registry does not know is excluded, and one it does
  know is decoded by that format's extractor rather than by the scanner (a staged container format
  yields its extractor's text, not its bytes); empty and whitespace-only files are skipped; the file
  id tracks the path while the hash tracks the content; an unreadable root throws rather than
  reporting an empty folder.
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

The embed window is tested on the property that lets it exist at all: the same corpus indexed at a window
of one, at the shipped default, and at a window larger than the whole corpus stores byte-identical vectors
under identical chunk ids. If that were ever untrue the window would silently be a correctness knob — the
same folder indexing differently depending on a number chosen for memory reasons, with nothing at query
time able to tell. Two further tests pin what it is for: chunks from many files are shown to arrive in one
call rather than one call per file, and a window below one is shown to fall back to a chunk at a time
rather than throwing, since zero and a negative fail differently without the clamp.

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
