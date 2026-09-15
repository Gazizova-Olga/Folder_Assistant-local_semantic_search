# SPEC-131 — Database Options Analysis

| | |
|---|---|
| Status | Reversed on evidence; see the verdict section |
| Version | 0.8.0 |
| Owner | Persistence |
| Last updated | 2026-09-15 |

## Purpose

Records which storage engines were considered for the manifest and the vectors, why plain
SQLite was chosen, and what would have to be true for that to change.

Written now rather than later because the decision is being made now, and a decision whose
alternatives were never written down is indistinguishable from a decision that was never
made.

## Decision

**Plain SQLite, vectors stored in an ordinary column, ranked by a brute-force scan** — as the
baseline every candidate is measured against, and as the store the default falls back to.

**As of 2026-09-15 the default profile runs the native `sqlite-vec` extension** where its binary
exists (win-x64, linux-x64, linux-arm64, osx-x64, osx-arm64), and the brute-force baseline where it
does not (win-arm64, musl). The baseline stays: it is what the candidate's ranking is checked
against, it is what runs on the platforms the candidate cannot, and it is what a `-blob` profile
names explicitly. The three conditions under "What would change the decision" were each met —
platform coverage is known and detected honestly (`SqliteVecExtension.PlatformHasBinary`,
asserted by the backend suite), the two backends agree on ranking up to ties (asserted by the
contract suite), and the latency gap is measured to grow with the corpus (below). What was *not*
met is a corpus size at which the linear read hurts — at the sizes measured neither is slow enough
for a model round-trip to notice — so the promotion rests on cost being near zero rather than on
the baseline being a problem: the fallback carries the platforms the binary does not.

## Constraints this has to satisfy

From [SPEC-130](SPEC-130-persistence.md) and the system concept:

- **One folder, one database, living inside that folder.** The index travels with the data
  it describes.
- **No service to deploy.** A tool that needs a database server running before it can answer
  a question about a local directory has the wrong shape.
- **Offline.** The default path must not require anything reachable over a network.
- **Several embedding models coexist**, keyed `(chunk_id, model_version_id)`.
- **One writer, many readers.** A background indexer writes; queries only read.
- Corpus size: a folder a person works in. Thousands of files, tens of thousands of chunks.
  Not millions.

## Criteria

| Criterion | Why it matters here |
|---|---|
| Deployment cost | Anything requiring a running service is close to disqualifying |
| Offline | The default path must work with no network |
| Fit with the folder-scoped model | The store has to live in the folder and be discardable with it |
| Retrieval quality headroom | Whether it can do better than a linear scan when the corpus grows |
| Concurrency | One writer, many readers, no cross-process coordination |
| Operational simplicity | Backup is "copy the file", ideally |
| Maturity | Whether it can be relied on without becoming the project |

## Options

### 1. Plain SQLite — **chosen**

Vectors in a column, similarity computed in process over every candidate.

Everything about the deployment story is right: one file, no service, offline, discardable
with the folder, backed up by copying. Write-ahead logging gives the one-writer/many-readers
pattern directly.

The cost is that ranking is O(n) per query. At the corpus sizes in scope, that is a scan of
tens of thousands of short vectors — fast enough that the embedding call dominates the query.

### 2. SQLite with a native vector extension

The same file and the same deployment story, plus indexed nearest-neighbour search.

Genuinely attractive and the most likely successor. Two reservations: the extension ships as
a per-platform native binary, so "runs anywhere .NET runs" stops being free and becomes a
matrix; and at the time of writing it is early. Worth revisiting with a benchmark rather
than an opinion.

### 3. SQLite through a higher-level vector connector

Adds a dependency whose abstraction is aimed at swapping cloud vector stores — which is not
the problem here. It would put a framework between the code and a file it already owns.

### 4. PostgreSQL with `pgvector`

Mature, well understood, good index support. Requires a server. That single fact removes it:
the product is a local folder assistant, and a database server in the install instructions
changes what the product is.

### 5. DuckDB

Embedded like SQLite, and stronger at analytical scans. But the workload is not analytical —
it is point lookups and a similarity scan — and its vector story is less settled than the
SQLite extension's. No advantage that pays for the change.

### 6. OpenSearch or Elasticsearch

Strong retrieval, including hybrid lexical and semantic. A JVM service, a cluster to
operate, and an index lifecycle to manage. Enormously out of proportion to one folder.

### 7. Self-hosted vector database

Purpose-built and fast. Also a service, a container, and a second data store to keep in step
with the manifest — reintroducing exactly the split-write problem the single-database design
exists to avoid.

### 8. Managed vector database

Removes the operational burden by moving the data off the machine. That breaks the offline
requirement and sends the contents of a user's folder to a third party. Not viable for this
product regardless of its merits.

### 9. Document databases with vector features

Same objection as the managed and self-hosted options: a service, and in the hosted case,
data leaving the machine.

### 10. SQL Server with vector support

Enterprise-grade, and irrelevant here for the same reason as PostgreSQL, with a heavier
footprint.

## Comparison

**Fit with the current design.** Only the two SQLite paths satisfy folder-scoped, offline,
no-service storage. Everything else fails at least one of those, and those three are not
preferences — they are what the product is.

**Retrieval headroom.** The vector-native options rank better as a corpus grows. That
matters at a scale this system does not currently target, and the point at which it starts
to matter is measurable rather than guessable.

**Operational simplicity.** SQLite is a file. Backup, restore, delete and copy are all
filesystem operations. Nothing else on this list gets close.

## Concurrency

The pattern is a single background writer and multiple readers, all in one process.
Write-ahead logging serves it directly: readers do not block the writer and the writer does
not block readers.

Two things to watch:

- **Connection-scoped settings.** In SQLite, several important settings are per connection
  rather than per database, so every connection has to establish them for itself.
- **Shared cache mode is a trap here.** It exists to let connections share an in-memory
  database. On a file-backed one it makes connections in a process share a cache, which
  turns contention into a failure the usual busy handling does not cover.

## Measured baseline

The brute-force scan exists partly to be the thing candidates are measured against, so here is
what it actually costs. Taken with `CorpusBenchmark` over a generated corpus of 4,000 files and
24,000 vectors at 64 dimensions, vectors stored as JSON text.

| stage | JSON + joins | after fixes | after blobs + split read |
|---|---|---|---|
| scan 4,000 files | 432 ms | 593 ms | 411 ms |
| index cold — 24,000 vectors embedded | 4.7 s | 5.8 s | 4.4 s |
| manifest read | **97,324 ms** | **731 ms** | 649 ms |
| re-index pass with nothing to do | **104.3 s** | **3.4 s** | 3.0 s |
| read + decode 24,000 vectors | 339 ms *(with joins)* | 329 ms *(with joins)* | **90 ms** *(no joins)* |
| cosine over 24,000 in-memory vectors | 25 ms | 24 ms | 15 ms |
| **retrieval p50** | 186 ms | 281 ms | **100 ms** |
| database size | 27 MB | 27 MB | 24 MB |

Three trees: the JSON baseline, the two query-shape fixes, and the packed-blob storage with the
split read contract. **Retrieval p50 varies run to run** — measured at 186 and 281 ms on identical
JSON-baseline code, so a retrieval change has to clear roughly ±50% to mean anything. The final
column is two samples that agreed (100 and 103 ms), which clears it.

Numbers from one machine, one corpus, one embedding dimension. They are recorded because the
*ratios* are the useful part and those are stable; the absolute milliseconds are not portable.

### Similarity search is not the bottleneck — but that does not close the extension question

**Cosine is about 15 ms of a 100 ms query.** It was 25 ms of 186 ms before. Both times the
arithmetic is a small minority of the cost, and the majority is getting the vectors into memory
at all: reading 24,000 rows and decoding them is ~90 ms of the ~100.

**A tempting inference is available here, and it is wrong.** "The arithmetic is only 15%, so a
native nearest-neighbour index can only win 15%" assumes the read is unavoidable. It is not — an
extension like `sqlite-vec` ranks *inside the database*, so a k-NN query never materialises the
other 23,995 vectors in the first place. What such a backend could attack is therefore closer to
the whole 100 ms than to the 15.

So the measurement **reframes** the question rather than settling it:

- It rules out the reason to adopt one that seemed obvious — that brute-force scoring is slow.
  It is not; 24,000 cosine similarities cost 15 ms.
- It leaves a real one standing: the read scales linearly with corpus size, and an indexed k-NN
  read does not.
- What it does not do is make the case, because 100 ms at 24,000 vectors is not a problem worth
  per-platform native binaries and an availability matrix ([Risks](#risks)) to solve. The corpus
  size where it becomes one has not been measured, and that measurement is the thing that would
  decide it.

The decision to build it therefore changed, and the reasoning is recorded below rather than
quietly replaced.

### The verdict reversed, and the earlier inference was the thing that was wrong

0.4.0 left exactly one thing outstanding: *"the corpus size where it becomes one has not been
measured, and that measurement is the thing that would decide it."* It has now been measured, by
building the backend and running both over the same corpus.

| | 24,000 vectors | 72,000 vectors |
|---|---|---|
| brute-force cosine, retrieval p50 | 106 ms | 325 ms |
| `sqlite-vec` k-NN, retrieval p50 | **4 ms** | **21 ms** |
| cold index | 5.2 s | 14.8 s |
| cold index, `sqlite-vec` | 5.4 s | 16.3 s |
| database size | 24 MB | 74 MB |
| database size, `sqlite-vec` | 23 MB | 69 MB |

"Database size" is the `.db` file alone. When these were taken, a write-ahead log about as large
as the database stayed beside it after a cold index — measured later at 4,000 files as 25,303 KB
beside 24 MB, and 23,798 KB beside 23 MB under `sqlite-vec` — so at that size the disk a freshly
indexed folder actually occupied was roughly double the first column. The 72,000-vector column was
not re-measured. A whole-folder pass now checkpoints when it finishes and leaves no log behind
([SPEC-130](SPEC-130-persistence.md)); the figures themselves are unchanged, because the log was
never in them.

**The measurement behind the earlier verdict was right; the inference from it was not.** Cosine
arithmetic really is a small minority of a query — 15 ms of 106, 49 ms of 325. The error was
reading that as a ceiling on what a native backend could win. `vec0`'s `k =` search is itself an
exact linear scan, so it performs the same comparisons; what it removes is *marshalling every
vector across the managed/native boundary* — a row, a `Byte[]` and a `Single[]` each, built only
to produce one number and be discarded. That was the read cost, and no storage format could have
removed it. **Reasoning about the algorithm predicted the wrong answer, and building both and
measuring gave the right one.**

That is the argument for implementation comparability being a product goal in its own right, which
is the reason for the modular monolith. Had this stayed rejected on paper, the 15x would never
have been found.

### What did *not* reproduce, recorded so it is not repeated

Indexing is **not** faster through this backend here — 16.3 s against 14.8 at 72,000 vectors, and
5.4 against 5.2 at 24,000. It is consistently a little slower. Only the retrieval side wins, and
only the retrieval claim is made.

### Adoption is a separate gate from existing

Passing a performance bar is what it takes to become the **default**. It is not what it takes to
**exist as a comparable implementation**, which needs no bar at all — it needs only to be
honestly measurable against the other one.

`sqlite-vec` is not the default, and speed is no longer the reason. It depends on an alpha native
extension shipping binaries for five RIDs only: there is no `win-arm64` build and no musl build,
so the backend does not exist everywhere the managed code runs. `SqliteVecExtension.IsAvailable`
reports that rather than failing at the first query. **Adoption now turns on dependency risk, not
on speed** — which is a different question from the one 0.4.0 was answering, and a better one to
be left with.
### Discovering there is no work costs more than doing all of it

A pass that embeds nothing took **104 seconds**; indexing the whole corpus from scratch took
**4.7**. Twenty-two times more expensive to conclude that nothing changed than to redo
everything — essentially all of it in the manifest read, which asks "does this file already have
vectors for the active model?" once per file with a correlated subquery.

**Fixed.** The vector-bearing file set is now materialised once and joined, instead of asked per
file: 731 ms, and the pass with nothing to do drops to 3.4 s. The companion fix replaced a
`NOT IN` over one bound parameter per scanned file with an indexed temp table, for the same
reason — SQLite rescans a long parameter list per row rather than building a lookup for it.

**And the read got cheaper too.** Vectors moved from JSON text to packed little-endian `float32`
(schema 3): a 64-dimension vector is 256 bytes rather than ~700-800 bytes of text that has to be
parsed one component at a time. Retrieval also stopped joining `chunk_manifest` and
`file_manifest` for candidates it was about to discard — locations are resolved after ranking,
for the handful of chunks that survive it. Together: read + decode 339 ms → 90 ms, retrieval p50
into the 100 ms range.

This was invisible at the size the rest of the suite works at, where folders hold three files.
It is the argument for having a benchmark at all.

## The other axis: which embedder finds the right document (2026-09-12)

Everything above measures the **backend** — how quickly the same vectors come back. It says nothing about
whether those vectors mean anything, and the two are independent: a fast read over a semantics-free
embedding is fast nonsense. This section measures the other axis, and measures the two backends against
each other for *agreement* as well as speed, because two implementations of one metric that disagree are
not a fast one and a slow one, they are a right one and a wrong one.

Instrument: `SemanticSearchBenchmark`, opt-in behind `RUN_SEMANTIC_BENCHMARK`, separate from the
large-corpus `CorpusBenchmark` used above. Reports in [docs/benchmarks/](../benchmarks/).

### Twenty documents, twenty-two paraphrase queries

Hand-written single-topic documents, and queries worded to avoid the distinctive vocabulary of the
document each one should find — so lexical overlap cannot carry them.

| Profile | dim | Recall@1 | Recall@3 | Recall@5 | MRR@10 | Index | Mean query | DB |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `programmable-blob` | 64 | 0% | 5% | 14% | 0.105 | 11 ms | 0.2 ms | 92 KB |
| `ollama-blob` | 1024 | **100%** | 100% | 100% | **1.000** | 6.9 s | 97.4 ms | 176 KB |
| `programmable-vec` | 64 | 0% | 5% | 14% | 0.105 | 19 ms | 0.8 ms | 388 KB |
| `ollama-vec` | 1024 | **100%** | 100% | 100% | **1.000** | 7.1 s | 114.8 ms | 4,232 KB |

1. **The placeholder sits at or below chance, which is exactly what makes it usable as a floor.** It puts
   the right document first for none of the twenty-two queries, where guessing among twenty documents would
   manage about one. That is the intended result (`SPEC-161`) and this is the first measurement of how far
   below a real embedder it sits — but note *why* it is this low here: the queries were written to deny it
   the shared vocabulary a character histogram can accidentally reward. On a corpus where queries quote
   their targets it would score far better and the number would mean far less.
2. **The pretrained embedder answers every one.** Recall@1 of 100% and MRR of 1.000 across all
   twenty-two, for a local CPU model. This is the evidence behind `SPEC-162`'s network dependency.
3. **The two backends rank identically.** Every accuracy column matches between `*-blob` and its `*-vec`
   twin. Sharing the vectors and the metric, they should — and this is the check that says so, rather than
   an assumption that they do.

### Three hundred documents, where a corpus-fitted embedder can fit

Generated from topic seeds: several documents per topic, each pairing differently worded variants of one
subject over identical neutral filler, so nothing but meaning distinguishes a topic. Every document of a
topic is relevant to that topic's queries.

| Profile | dim | P@1 | MAP | nDCG@10 | Recall@10 |
|---|---:|---:|---:|---:|---:|
| `programmable-blob` | 64 | 7% | 0.031 | 0.071 | 7% |
| `lsa-blob` | 32 | 45% | 0.401 | 0.451 | 44% |
| `ollama-blob` | 1024 | **82%** | **0.675** | 0.748 | 72% |

4. **The corpus-fitted embedder is real, and it is weak — which is what `SPEC-161` claims.** Thirteen times
   the placeholder's mean average precision, and a little over half the pretrained model's. That is the
   first evidence for the synonymy claim on a corpus of realistic size; everything supporting it before was
   a nine-document fixture. It does not make LSA a default, and it does establish that fitting buys
   something real rather than nothing.
5. **The accuracy columns reproduced exactly across three runs**, while every latency moved. That is the
   separation worth having: the accuracy figures are properties of the models and the corpus, not of the
   machine or the hour.

### The backend gap widens with the corpus — and reverses below a threshold

| Docs | `blob` p50 | `vec` p50 | `blob` p95 | `vec` p95 |
|---:|---:|---:|---:|---:|
| 20 | 0.2 ms | 0.8 ms | — | — |
| 500 | 1.4 ms | 0.3 ms | 2.0 ms | 0.5 ms |
| 2,000 | 6.0 ms | 0.5 ms | 7.3 ms | 1.4 ms |
| 8,000 | 34.1 ms | 1.7 ms | 47.0 ms | 3.9 ms |

6. **Below a few hundred documents the native backend is the slower one.** At twenty documents `vec` costs
   more per query than reading every vector does, because its fixed cost has nothing to amortise against.
   This matters more than the headline ratio for a decision about a per-platform binary: the folder a
   person actually points this at may well sit on the wrong side of that crossover.
7. **Above it, the gap grows with the corpus** — roughly an order of magnitude by 8,000 documents. The
   exact multiple should not be quoted to a significant figure: across three runs of the same commit it
   measured 12.7x, 16.2x and 20.1x. The direction and the order of magnitude are the findings; the digits
   are not, and a single run of this would have reported any one of them as though it were the answer.
8. **The footprint penalty is concentrated on small corpora, not inherent.** At twenty documents and 1,024
   dimensions the `vec0` table costs 4,232 KB against the blob store's 176 KB — twenty-four times. At 64
   dimensions across 500 to 8,000 documents it is 1.16x, 0.94x and 0.93x, so at scale it is level with the
   blob store and then slightly smaller. Only those two cases were measured; nothing here says what 1,024
   dimensions costs at scale, and it should not be read as if it did.

## Risks

| Path | Risk |
|---|---|
| Plain SQLite | **Not linear ranking** — measured at ~15% of a query over 24,000 vectors. The cost is reading and decoding every stored vector, which does scale with the corpus and *is* what an indexed k-NN read avoids. At this size it is 90 ms; the size at which it matters has not been measured |
| Native extension | Per-platform binaries; availability becomes a matrix, and "unavailable" must be detected honestly rather than guessed |
| Any service-backed store | Contradicts the product; two stores to keep consistent |
| Any hosted store | Sends folder contents off the machine |

## What would change the decision

- A corpus size at which the linear read becomes a problem. That is the open number: the read
  scales with corpus size and an indexed k-NN read does not, but at 24,000 vectors it is 90 ms,
  which does not justify per-platform native binaries. Nobody has measured where it would.
- A measured query latency that the embedding call no longer dominates — and, now that the
  breakdown exists, one where the similarity arithmetic is a large enough share of it to be
  worth attacking. At 13% it is not.
- A native extension whose platform coverage is known and whose unavailability can be
  detected reliably, beating the brute-force baseline on the same vectors and the same
  metric — including agreeing with it on ranking, up to ties.

The last two were met by 2026-09-15 and the default moved to the native extension on that basis
(Decision, above); the first is still the open number. The baseline stands as the thing candidates are
compared against, and as the fallback on the platforms the extension does not reach.

## References

- [SPEC-130 — Persistence](SPEC-130-persistence.md)
- [Single-database design and model evolution](../diagrams/single-db-table-design-and-model-evolution.md)
