# SPEC-130 — Persistence

| | |
|---|---|
| Status | Draft |
| Version | 0.4.0 |
| Owner | Persistence |
| Last updated | 2026-09-08 |

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

**Cleanup of stale or orphaned rows is not implemented.** Rows are retained until explicit
cleanup exists.

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
- **Concurrency** — write-ahead logging and foreign keys are enabled at bootstrap. Note
  that both are connection-scoped in SQLite except for the journal mode, which is stored in
  the file.
- **Operability** — bootstrap reports whether it created or reused the database, and where.

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
