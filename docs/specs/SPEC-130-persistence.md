# SPEC-130 — Persistence

| | |
|---|---|
| Status | Draft |
| Version | 0.15.0 |
| Owner | Persistence |
| Last updated | 2026-09-27 |

## Purpose

Owns the folder-scoped database: its schema, its transactions, and the rules that keep
concurrent readers and writers correct.

## Scope

**In scope**

- Locating and creating the database for an analyzed folder.
- Schema definition and its forward migration path.
- Transaction boundaries and connection policy.
- Repository contracts for the manifest, chunks and vectors, on both the read and write side.

**Out of scope**

- What is embedded and how ([SPEC-160](SPEC-160-embedding-module.md)).
- Conversation content ([SPEC-100](SPEC-100-conversation-orchestration.md)).

## Implementation status

Startup bootstrap is implemented: the application resolves the folder-scoped database,
creates it when missing, and ensures the schema on every run. The manifest, chunk, vector
and model-registry tables are created idempotently.

## Requirements

### Bootstrap

- The database lives in a metadata subfolder **inside the analyzed folder**, so the index
  travels with the folder it describes.
- The metadata folder and the database file are created on first run.
- The schema is ensured on **every** run, not only at creation.
- Bootstrap runs **synchronously at startup, before any request is handled**. A query
  against a database that does not yet exist is not a case worth supporting. The ordering is
  enforced by an `IStartupFilter`, which the host resolves before the server starts listening.
  It is deliberately not run inline at the end of `Main`: the configuration is not final until
  the host is built, and a test host intercepts at that point, so an inline call would hold in
  production and silently not hold anywhere it was checked
  ([SPEC-100](SPEC-100-conversation-orchestration.md)).

### Contracts

```
IFolderDatabaseBootstrapper.EnsureInitialized(analyzedFolderPath, config)
    -> DatabaseBootstrapResult(DatabasePath, Created)
```

`Created` reports whether **this call** brought the database into existence.

### Error model

- An invalid path or configuration is an argument error, thrown before anything is opened.
- A failure to open or initialise the database propagates and stops startup. A process that
  starts without its index is a process that will answer questions wrongly.

## Data model

Tables created at baseline:

| Table | Holds |
|---|---|
| `schema_version` | One row. The version this database was created at |
| `file_manifest` | One row per indexed file |
| `chunk_manifest` | One row per chunk, with its token window |
| `embedding_model_registry` | One row per embedding model version |
| `chunk_vector` | The vectors |
| `embedding_fit_artifact` | The persisted fit for a corpus-dependent model, where there is one |
| `outbox` | One row per queued delivery: what changed, and what has been tried |

**The schema is created whole rather than grown a table at a time.** One of its invariants
cannot be retrofitted: `chunk_vector` is keyed `(chunk_id, model_version_id)` so that
several embedding implementations can coexist in one database. A schema grown per feature
would have keyed it on `chunk_id` alone, and introducing a second embedder would then mean
migrating every vector already stored.

Deleting a file cascades to its chunks and their vectors. Indexes cover file-path lookup,
chunk-by-file lookup and vector-by-model lookup.

### The model registry activation invariant

**Exactly one row in `embedding_model_registry` may have `is_active_for_write` set.**

Registering a model version activates it and demotes every other row, in the same
transaction. Without the demotion, indexing a folder with a second model leaves two rows
both claiming to be active, and nothing downstream can determine which vectors are current
— which is precisely the state that arises when comparing two embedding implementations
against one corpus, the thing the composite vector key exists to make possible.

The registry row is written from the active vectorizer's own descriptor
([SPEC-160](SPEC-160-embedding-module.md)): provider, model name, dimension and distance
metric all come from the implementation rather than from configuration.

**Orphan cleanup is implemented** ([SPEC-120](SPEC-120-rag-indexing.md) delta handling): a
manifest row whose file is gone is marked by the indexing store and queued, and the delivered
removal clears its vectors, then the row, whose cascade takes the chunks
([SPEC-121](SPEC-121-file-indexing-front-end.md)); chunks superseded by an edit are removed before
the new ones are written. Foreign keys are enabled on the writing connection, since they are per
connection in SQLite and what the bootstrap set does not carry.

**`file_manifest` has one writer of what a row says: the indexing store.** The embedding side —
the whole-folder pass and the per-file delivery — writes chunks and vectors under a row's id and ends
a row on a delivered removal, and has no statement that could create or update one. The foreign key
from `chunk_manifest` is what enforces it: chunks for an id no row carries are refused, not invented.

## Configuration

| Key | Default |
|---|---|
| `FolderAssistant:Persistence:AnalyzedFolderPath` | the current working directory |
| `FolderAssistant:Persistence:MetadataFolderName` | `.folderassistant` |
| `FolderAssistant:Persistence:DatabaseFileName` | `manifest.db` |

## Non-functional requirements

- **Reliability** — startup always guarantees the required tables exist. An existing
  database is reused; missing objects are created without destructive change.
- **Compatibility** — the manifest, chunk and vector tables stay usable when the embedding
  implementation changes between runs.
- **Concurrency** — see the rules below; the summary is one writer, many readers, under WAL,
  with no shared cache.
- **Operability** — bootstrap reports whether it created or reused the database, and where.


## Concurrency rules

- The access pattern is **writers on background threads, readers on request threads**, against
  one file. The writers are the whole-folder pass — once, at start, in a single transaction — and
  then the front end's record-and-queue writes and the dispatcher's deliveries
  ([SPEC-121](SPEC-121-file-indexing-front-end.md)), several threads each opening a short-lived
  connection, serialised by the database's own write lock and the busy timeout every connection
  sets. The pass and the front end never run at the same time
  ([SPEC-120](SPEC-120-rag-indexing.md)). Retrieval reads on request threads.
- **WAL is what makes that legal**, and is the only reason a read does not block behind an
  index in flight. It is set once at bootstrap: `journal_mode` is persisted in the database
  file rather than being a property of a connection.
- **WAL has a cost, and it is reclaimed at exactly two moments.** A committed write lives in the
  log until a checkpoint copies it back, and SQLite's automatic checkpoint runs on a page
  threshold while writing and never shrinks the log file. Measured with `CorpusBenchmark` at
  4,000 files, a whole-folder pass left a **25,303 KB log beside a 24 MB database** — a second
  copy of about everything it had written — and **23,798 KB beside 23 MB** under `sqlite-vec`.
  A truncating checkpoint now runs when a whole-folder pass finishes; after it, the same
  measurement reads **0 KB** for both, in two runs. The outbox dispatcher invites one at the
  other quiet moment — a drain going quiet after delivering work (`SPEC-121`) — and
  `FolderIndexStore` takes it, through the same statement; that half has no figure of its own,
  because the burst it follows is one changed file at a time rather than a corpus. Immediately
  before it the dispatcher asks the same store to drop the rows of deliveries that succeeded, so
  the checkpoint reclaims that space in the same pass. Checkpointing anywhere else is **ruled out**: per write it blocks readers
  again and again to reclaim the same space, and on an idle poll it would run forever against
  a folder nobody is touching. It is also **advisory** — a checkpoint SQLite cannot take right
  now (a reader still on an older snapshot) reports busy rather than failing, costs disk only,
  and must never surface as an error in the loop that asked for it. The statement lives once,
  in `FolderDatabaseMaintenance`.
- **`PRAGMA foreign_keys` and `PRAGMA busy_timeout` are per-connection and are not
  persisted**, so every connection sets them for itself immediately after opening.
  `foreign_keys` additionally cannot be set inside a transaction, and cascading deletes
  silently do nothing without it — no error, just rows that quietly stay behind.
- **Shared-cache mode must not be used.** It exists to share an *in-memory* database between
  connections. On a file-backed database it makes every connection in the process share one
  cache, and concurrent use then faults inside `sqlite3_prepare_v2` when the GC finalizes a
  handle while another connection is preparing a statement. It also converts contention into
  `SQLITE_LOCKED`, for which the busy handler is never invoked — so it quietly defeats the
  busy timeout as well.
- **Connection pooling is off**, on every connection, by decision (below): the pool hands one
  `sqlite3` handle to two threads about once in 1,500 concurrent opens of one file, and an open
  without it costs about 0.35 ms on the development machine, which every caller pays per operation.
- **All connections open through one helper** (`FolderDatabaseConnection`), so these settings
  cannot drift apart between call sites. They already had: the shared-cache flag reached all
  four sites by someone copying a working connection string, and the writer ran with no busy
  timeout at all, because only the bootstrapper had ever set one.
- Regression guard: `PersistenceConcurrencyTests` — concurrent bootstraps under GC pressure,
  and retrieval reading while the indexer writes.

### Decided: connection pooling is off, because the pool shares a handle between threads

Until 0.14.0, `Concurrent_Bootstraps_Of_The_Same_Folder_Never_Fault` went red in roughly one run
in five (**measured: 3 red in 15, and 1 in 10, in isolation**; three in about eight full runs on
2026-09-16), with `SQLITE_ERROR` (code 1) raised from `BeginTransaction` inside
`FolderDatabaseBootstrapper`. What follows is the finding as it was recorded, then the decision.

**It is not the shared-cache fault and not a busy-timeout shortfall.** It is never
`SQLITE_BUSY`, and the busy handler is not invoked for `SQLITE_ERROR`, so the timeout every
connection sets is not involved. The connection stays usable afterwards.

**It is the connection pool.** Measured by driving the failing shape — eight concurrent
bootstraps of one folder — and counting failures rather than red runs, alternating passes so
machine drift fell on both sides:

| connection string | failures |
|---|---|
| pooled (the default) | **70 in 100,000 bootstraps** |
| `Pooling=False` | **0 in 40,000 bootstraps** |

Two further results from the same measurement. **`GC.Collect()` is not the trigger** — with it
removed the fault persists at 25 and 5 failures per 20,000, against 15 with it — so the collect
in that test guards the shared-cache fault only, and stays. And **the error text shows a shared
handle**: alongside `'SQL logic error'` and `'cannot start a transaction within a transaction'`
came `'not an error'` and `'database schema has changed\0ithin a transaction'`, one error buffer
holding two messages spliced by an embedded NUL. That is one `sqlite3` handle in use by two
threads at once, which is also what the `ObjectDisposedException`-inside-SQLite shape seen
elsewhere in the suite looks like from managed code: **one fault, two presentations.**

What is *not* established is the mechanism inside `Microsoft.Data.Sqlite` (10.0.9) that lets a
pooled handle be reached twice. Only that pooling is required for the fault, which is as far as
the measurement reaches.

**Decided 2026-09-16: `Pooling=False` on every connection**, set in the one factory. Two candidates
were on the table — no pool, or a retry around `BEGIN` — and the retry was never a fix: it would
hide this presentation of a shared handle while leaving the others, including the one that
corrupts a reader mid-statement. What settled the cost side was re-measuring both on the tree that
takes the decision, driven the same way — eight concurrent bootstraps of one folder, 4,000 rounds
— but with **no pool clearing anywhere in the process** (one temp folder, one subdirectory per
round, nothing disposed until the end), so that the suite's own folder cleanup could not be what
triggered it:

| connection string | faults in 32,000 bootstraps | one open, pragmas and one query |
|---|---|---|
| pooled | **1** (`SQL logic error` out of `BEGIN`, round 3,095) | 8–12 µs |
| `Pooling=False` | **0** | 350–390 µs |

The fault happens with nothing clearing pools, so it is the pool's and not the suite's, and a
running application — one pool, the dispatcher writing while request threads read — shares the
exposure.

The price is more than the open. A pooled connection kept its page cache warm between operations; a
fresh one reads its pages again. `CorpusBenchmark` at 4,000 files, run both ways on the tree that
takes the decision, one run each (scan time varied 2.9–3.9 s between runs, so treat the figures as
the shape, not the number):

| figure | pooled | `Pooling=False` |
|---|---|---|
| blob retrieval p50 over 24,000 vectors | 109 ms (97–112) | 119 ms (111–134) |
| `sqlite-vec` retrieval p50 | 6 ms (6–6) | 15 ms (14–17) |
| `sqlite-vec` cold index, 4,000 files | 4.9 s | 5.5 s |
| write-ahead log left after the pass, both stores | 0 KB | 0 KB |

About ten milliseconds on a query and a tenth more on a cold index with the placeholder embedder —
beside a model turn measured in seconds and an embed measured at hundreds of milliseconds per chunk.
None of that is a reason to keep a handle two threads can hold. The test keeps its concurrency;
eight concurrent bootstraps is what makes the property testable at all.

One consequence for the checkpoint rule above: with no pool, the last connection to close folds the
log away itself, so between operations the log is gone whether or not the truncating checkpoint ran.
The checkpoint keeps its two moments for the case it was written for — a reader holding a
connection across the pass — and the benchmark's log figure now reads zero unless a connection was
left open, which is what makes it still worth printing.

**The pool had a second, non-flaky symptom**, worth knowing because it is silent: a disposed
connection returned to the pool still holding its database file open, so a test directory containing
a database could not be deleted straight away. The factory's connections no longer do that; the
tests that open a raw `SqliteConnection` of their own still pool, so `TempFolder` still closes the
pools and retries.

## Migration

**Schema version 4** makes room for deliveries. `file_manifest` gains two nullable columns — the
file's own creation time, and the content hash of what was last delivered for embedding — and a new
`outbox` table holds one row per queued delivery. An existing database picks the table up through
the idempotent `CREATE`; the columns need an `ALTER`, which is what makes this a migration rather
than a no-op, and the version is bumped only when one is actually added.

**Both columns are nullable, and that is the honest shape rather than a convenience.** A row written
before either existed has no creation time recorded, and has had nothing delivered under this
bookkeeping. A default would state something about that row which nobody knows, and the first reader
would believe it.

**The outbox is keyed on the path, not on a file record.** An operation has to outlive the thing it
describes: a deletion is deliverable precisely when the record it came from is gone. Ordering is by
the row id, which is what lets one file's operations run in the order they were queued.

**Rows for deliveries that succeeded are discarded; rows for deliveries given up on are kept.** The
dispatcher asks for the prune at the same quiet moment as the checkpoint and before it, and the store's
statement is one `DELETE` of delivered rows older than the window
([SPEC-121](SPEC-121-file-indexing-front-end.md) holds the rules and the reasons). Two properties belong
to this spec:

- **The window is measured from `created_utc`**, because it is the only time the table records. There is
  no completion column, and adding one is a schema version — worth spending only if these rows turn out
  to be read for diagnosis, which is not yet known.
- **A failed row is index state, not queue bookkeeping.** It is the one record that a file the folder
  holds is not in the index, so nothing ages it out and nothing depends on anyone having read it.

**Schema version 3** stores vectors as packed little-endian `float32` in `chunk_vector.vector`,
replacing `vector_json TEXT`. **This one is the first real migration**, because an idempotent
`CREATE TABLE IF NOT EXISTS` cannot change a column's type: bootstrap detects the old column,
reads the JSON vectors, rebuilds the table and writes them back packed.

Vectors are **re-encoded, not discarded.** They are derived state and could be rebuilt from the
folder, but rebuilding means re-embedding every chunk — the most expensive thing the system does
— and it would strand the stored fit artifact, which has to stay consistent with the vectors
produced under it. The whole migration runs inside the bootstrap transaction: a half-migrated
`chunk_vector` is indistinguishable from a corrupt one, and there would be no way to tell which
had happened.

**Schema version 2** adds `embedding_fit_artifact`. An existing version 1 database picks the
table up through the idempotent `CREATE`; what the version bump changes is only the value
seeded into a database created from now on.

Forward migrations key off `schema_version.version`. The baseline uses idempotent table and
index creation; versioned migrations are added when the first breaking change arrives.

Recovery is currently limited to restoring the database file. Nothing automates it.

## Open questions

- Whether the database file name should carry a hash of the folder path, so an index
  survives the folder being moved.

## References

- [Single-database design and model evolution](../diagrams/single-db-table-design-and-model-evolution.md)
- [SPEC-131 — Database options analysis](SPEC-131-database-options-analysis.md) — why SQLite, and what would change it
- [SPEC-160 — Embedding module](SPEC-160-embedding-module.md)
