# SPEC-131 — Database Options Analysis

| | |
|---|---|
| Status | Decided for now, open to evidence |
| Version | 0.4.0 |
| Owner | Persistence |
| Last updated | 2026-09-09 |

## Purpose

Records which storage engines were considered for the manifest and the vectors, why plain
SQLite was chosen, and what would have to be true for that to change.

Written now rather than later because the decision is being made now, and a decision whose
alternatives were never written down is indistinguishable from a decision that was never
made.

## Decision

**Plain SQLite, vectors stored in an ordinary column, ranked by a brute-force scan.**

This is the baseline. It is not assumed to be the endpoint: any candidate below is welcome
to replace it on measured evidence, and the brute-force scan exists partly to be the thing
that evidence is measured against.

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

The decision therefore stands unchanged, on narrower and better-understood grounds than before.
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

Until then the baseline stands, and stays as the thing candidates are compared against.

## References

- [SPEC-130 — Persistence](SPEC-130-persistence.md)
- [Single-database design and model evolution](../diagrams/single-db-table-design-and-model-evolution.md)
