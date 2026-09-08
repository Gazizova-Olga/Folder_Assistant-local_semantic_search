# One Database, Several Tables, and Surviving a Model Change

## Conclusion first

One SQLite database per analyzed folder, several tables inside it, and vectors keyed by
embedding model as well as by chunk. That last part is the whole point of this document:
it is the difference between introducing a second embedding model and migrating every
vector already stored.

## Why one database is enough here

The access pattern is one writer and many readers, over a corpus bounded by a single
folder. That is squarely inside what SQLite is good at, and it buys properties that are
awkward to get otherwise:

- **The index travels with the folder.** Delete the folder, the index goes with it. Copy
  it, and it arrives indexed.
- **One transaction spans the manifest, the chunks and the vectors.** They are only
  meaningful together.
- **No service to run.** Nothing to start, configure, or fail to reach.

The cost is that write concurrency is coarse. For a background indexer and a query path
that only reads, that is an acceptable trade.

## One physical database, several logical domains

The tables below are separate concerns that happen to share a file. Keeping them in one
file is a deployment decision; keeping them in separate tables is a modelling one.

## The table set

### `file` manifest

One row per indexed file: identity, path, content hash, size, timestamps, status.

The content hash is what makes re-indexing cheap — a file whose hash is unchanged has
nothing to re-embed.

### `chunk` manifest

One row per chunk: identity, owning file, its position in the token stream, its hash.

**Chunks store no text.** The token window is recorded, and the text is rebuilt from the
file when it is needed. Storing it again would double the corpus on disk to save a read.

### `embedding_model_registry`

One row per embedding model version: provider, model name, dimension, distance metric, and
whether it is the one currently active for write.

**Exactly one row may be active.** Registering a model demotes the others in the same
transaction. Without that, indexing a folder with a second model leaves both rows claiming
to be active, and nothing downstream can tell which vectors are current.

### `chunk_vector`

The vectors, keyed `(chunk_id, model_version_id)`.

The composite key is the load-bearing decision in this whole design. Keyed on `chunk_id`
alone, introducing a second embedding model would mean migrating every vector already
stored, and there would be no moment at which both existed to be compared.

### Tables considered and deferred

- **Retrieval audit** — every query and what it returned. Useful for measuring retrieval
  quality; not needed to make retrieval work. Deferred until there is something to measure.
- **Indexing job history** — one row per indexing run. Same reasoning.
- **File event inbox** — durable queue for file-change events. Worth having once indexing
  is incremental and event-driven; premature while it is a full pass.

## What must not be duplicated

**File text.** It is on disk already. The manifest records where and what hash; the content
is read when needed.

**Chunk text.** Derivable from the file and the recorded token window.

The temptation in both cases is to store it "so retrieval is fast". The corpus is local
disk. It is already fast.

## Changing the embedding model safely

The registry and the composite vector key exist to make this a routine operation rather
than a migration.

**1. Register.** Insert the new model version. It becomes active for write; the previous
one is demoted but its vectors stay exactly where they are.

**2. Index.** New and changed content is embedded with the new model. Old vectors remain
readable, and remain the only vectors for content not yet reprocessed.

**3. Backfill.** Re-embed the rest of the corpus under the new model version, at whatever
pace suits. Both sets coexist throughout.

**4. Retire.** Once nothing queries the old version, delete its vectors. This is the only
destructive step, and it is reversible up to the point it is taken by simply not taking it.

**A query is scored only against vectors sharing its model version.** Vectors from
different models occupy different spaces; a similarity computed across them is a number
with no meaning. Querying a model version that was never indexed returns nothing, rather
than silently falling back to another model's vectors.

## References

- [SPEC-130 — Persistence](../specs/SPEC-130-persistence.md)
- [SPEC-160 — Embedding module](../specs/SPEC-160-embedding-module.md)
- [SPEC-900 — Versioning and migration](../specs/SPEC-900-versioning-and-migration.md)
