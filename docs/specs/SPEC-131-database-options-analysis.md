# SPEC-131 — Database Options Analysis

| | |
|---|---|
| Status | Decided for now, open to evidence |
| Version | 0.2.0 |
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

| stage | measured |
|---|---|
| scan 4,000 files | 432 ms |
| index cold — 24,000 vectors embedded | 4.7 s |
| manifest read (is this file already vectorised?) | **97,324 ms** |
| re-index pass with nothing to do | **104.3 s** |
| read + parse 24,000 vectors, with location joins | 339 ms |
| **cosine over 24,000 in-memory vectors** | **25 ms** |
| retrieval p50 | 186 ms |

Numbers from one machine, one corpus, one embedding dimension. They are recorded because the
*ratios* are the useful part and those are stable; the absolute milliseconds are not portable.

### Similarity search is not the bottleneck

**Cosine is 25 ms of a 186 ms query — about 13%.** The remaining 87% is reading rows, parsing
JSON vectors component by component, and joining `chunk_manifest` and `file_manifest` to locate
candidates, almost all of which are then discarded.

An approximate nearest-neighbour index can only attack the 25 ms. That is the whole of what it
would buy, and it costs per-platform native binaries and an availability matrix
([Risks](#risks)) to buy it. **The stored representation and the read shape are the real
targets**, and they are addressed in the two commits following the one that took these numbers.

### Discovering there is no work costs more than doing all of it

A pass that embeds nothing takes **104 seconds**; indexing the whole corpus from scratch takes
**4.7**. Twenty-two times more expensive to conclude that nothing changed than to redo
everything — essentially all of it in the manifest read, which asks "does this file already have
vectors for the active model?" once per file with a correlated subquery.

This is invisible at the size the rest of the suite works at, where folders hold three files.

## Risks

| Path | Risk |
|---|---|
| Plain SQLite | **Not linear ranking** — measured at 13% of a query over 24,000 vectors. The cost is reading and parsing stored vectors and joining to locate them, which grows the same way and is not what an ANN index addresses |
| Native extension | Per-platform binaries; availability becomes a matrix, and "unavailable" must be detected honestly rather than guessed |
| Any service-backed store | Contradicts the product; two stores to keep consistent |
| Any hosted store | Sends folder contents off the machine |

## What would change the decision

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
