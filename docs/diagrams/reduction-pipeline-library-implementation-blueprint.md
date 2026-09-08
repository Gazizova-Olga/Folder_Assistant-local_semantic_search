# Reduction Pipeline — Implementation Blueprint

How a question about a folder becomes a bounded amount of context in front of a language
model, and how the index that makes that possible is kept current.

## The functional goal

A folder can be far larger than any context window. The pipeline's job is to put the few
passages that matter in front of the model, and to be honest about it when there are none.

"Reduction" is the whole point: not retrieving everything relevant, but retrieving the
best of it and stopping at a budget.

## Responsibilities

| Group | Owns |
|---|---|
| Ingest | Walking the folder, filtering, hashing, chunking |
| Embed | Turning chunk text into vectors through a replaceable implementation |
| Store | The manifest, chunks, model registry and vectors, in one transaction |
| Retrieve | Query embedding, ranking, assembly under a budget |

## Minimum data model

**File** — identity, relative path, content hash, size, modified time, status.

**Chunk** — identity, owning file, token window, hash. No text: the window plus the file is
enough to rebuild it.

**Vector** — keyed `(chunk_id, model_version_id)`, with its dimension recorded alongside.

**Request trace** — optional, deferred. Worth adding when retrieval quality is being
measured; not needed to make retrieval work.

## Request-time algorithm

1. Embed the query text with the active model, as a *query* rather than a document — some
   models treat the two sides differently.
2. Rank stored vectors of that same model version. Never across versions: vectors from
   different models occupy different spaces.
3. Take a candidate pool larger than the final answer, so there is something to select from.
4. Assemble: rank by relevance, keep the result set diverse enough to be worth reading, and
   stop at the token budget.
5. Rebuild the passage text for the survivors, from their files.
6. If nothing clears the floor, say so. An empty answer is a result; a plausible-looking
   irrelevant one is a failure that is hard to see.

## Index-time algorithm

1. Scan the folder, filtered by extension and size.
2. Hash each file. Unchanged hash, nothing to do.
3. Chunk changed files with a fixed window and overlap.
4. Embed the chunks.
5. Write files, chunks and vectors in one transaction.

**The scan is authoritative for deletion**, which is why it must fail loudly rather than
return an empty result. A scan that silently returns nothing would clear the index.

## Quality gates

- A fitted embedding implementation out-ranks the deterministic placeholder on a labelled
  set. If it does not, its fitting is not doing anything.
- Two storage backends holding the same vectors rank a query identically, up to ties.
- Re-indexing an unchanged folder writes no new rows.
- Indexing under a second model leaves exactly one model active for write.

## Rollout

A default changes on benchmark evidence. The previous implementation stays selectable, and
the vectors it produced stay readable — that is what the model registry is for.

## Fallback behaviour

- **Index still building** — refuse the query rather than answer from a half-built index.
  Results from a partial index are indistinguishable from genuinely poor ones.
- **Embedding backend unreachable** — fail the call, surface it, retry on a schedule.
  Silently indexing nothing is the worst available outcome.
- **No chunk clears the relevance floor** — return nothing, with a note saying why.

## References

- [Library architecture](library-architecture.md)
- [SPEC-110 — Retrieval](../specs/SPEC-110-rag-retrieval.md)
- [SPEC-120 — Indexing](../specs/SPEC-120-rag-indexing.md)
