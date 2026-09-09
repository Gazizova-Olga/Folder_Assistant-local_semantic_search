# SPEC-120 — Indexing

| | |
|---|---|
| Status | Draft |
| Version | 0.3.0 |
| Owner | Indexing |
| Last updated | 2026-09-09 |

## Purpose

Enumerates files, splits their text into chunks, and keeps the index in step with the folder
as it changes.

## Scope

**In scope**

- File enumeration and filtering.
- Tokenization and chunking.
- Deciding what has changed since the last pass, and acting on it.
- When a pass runs, and what retrieval is told while one has not finished.

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
is the honest answer.

## Sharp edge — deletion is inferred from the scan

The scanned file list is treated as the authoritative current state of the folder. **A scan that
silently returned nothing would clear the index.**

This is currently sound because the scanner throws on a bad path rather than returning an empty
result, and that property is therefore load-bearing rather than defensive. Anything that later
makes the scan partial — a permissions error swallowed per directory, a cancelled walk returning
what it had — breaks this without any test necessarily noticing.

## Non-functional requirements

- **Reliability** — re-indexing an unchanged folder writes no new vectors and loses nothing.
- **Performance** — cost scales with what changed, not with corpus size, for the embedding stage.
- **Operability** — a pass reports scanned, indexed, unchanged and deleted counts, so "nothing
  happened" and "nothing needed to happen" are distinguishable.

## Open questions

- Whether fit drift should be detected, and what the signal would be.
- Whether a partial scan should be distinguishable from a complete one, so deletion can be made
  conditional on the scan being known-complete.

## References

- [SPEC-130 — Persistence](SPEC-130-persistence.md)
- [SPEC-160 — Embedding module](SPEC-160-embedding-module.md)
- [Reduction pipeline blueprint](../diagrams/reduction-pipeline-library-implementation-blueprint.md)
