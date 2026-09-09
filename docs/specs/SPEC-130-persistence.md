# SPEC-130 — Persistence

| | |
|---|---|
| Status | Draft |
| Version | 0.6.0 |
| Owner | Persistence |
| Last updated | 2026-09-09 |

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
  against a database that does not yet exist is not a case worth supporting.

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
manifest row whose file is gone is removed and its chunks and vectors cascade away, and chunks
superseded by an edit are removed before the new ones are written. Foreign keys are enabled on
the writing connection, since they are per connection in SQLite and what the bootstrap set does
not carry.

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

- The access pattern is **one writer, many readers**. The indexing service writes on a
  background thread — its passes serialized ([SPEC-120](SPEC-120-rag-indexing.md)) — while
  retrieval reads on request threads, against the same database file.
- **WAL is what makes that legal**, and is the only reason a read does not block behind an
  index in flight. It is set once at bootstrap: `journal_mode` is persisted in the database
  file rather than being a property of a connection.
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
- **All connections open through one helper** (`FolderDatabaseConnection`), so these settings
  cannot drift apart between call sites. They already had: the shared-cache flag reached all
  four sites by someone copying a working connection string, and the writer ran with no busy
  timeout at all, because only the bootstrapper had ever set one.
- Regression guard: `PersistenceConcurrencyTests` — concurrent bootstraps under GC pressure,
  and retrieval reading while the indexer writes.

### Known: concurrent bootstrap faults intermittently

`Concurrent_Bootstraps_Of_The_Same_Folder_Never_Fault` goes red in roughly one run in five
(**measured: 3 red in 15 runs, in isolation**), with `SQLITE_ERROR` (code 1, "SQL logic
error") raised from `BeginTransaction` inside `FolderDatabaseBootstrapper`.

**This is not the shared-cache fault and not a busy-timeout shortfall.** It is never
`SQLITE_BUSY`, and the busy handler is not invoked for `SQLITE_ERROR`, so the timeout every
connection now sets is not involved. The connection stays usable afterwards.

The cause is not established and nothing here fixes it. It is recorded rather than left for
someone to rediscover as an unexplained red build — and it must not be "fixed" by lowering
the test's concurrency, which is the only thing making the property testable at all.

## Migration

**Schema version 2** adds `embedding_fit_artifact`. An existing version 1 database picks the
table up through the idempotent `CREATE`; what the version bump changes is only the value
seeded into a database created from now on.

Forward migrations key off `schema_version.version`. The baseline uses idempotent table and
index creation; versioned migrations are added when the first breaking change arrives.

Recovery is currently limited to restoring the database file. Nothing automates it.

## Open questions

- Whether vectors should stay JSON text until a native vector extension is worth adopting,
  or move to a packed binary representation earlier.
- Whether the database file name should carry a hash of the folder path, so an index
  survives the folder being moved.

## References

- [Single-database design and model evolution](../diagrams/single-db-table-design-and-model-evolution.md)
- [SPEC-131 — Database options analysis](SPEC-131-database-options-analysis.md) — why SQLite, and what would change it
- [SPEC-160 — Embedding module](SPEC-160-embedding-module.md)
