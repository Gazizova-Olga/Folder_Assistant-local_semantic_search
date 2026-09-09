# SPEC-110 — Retrieval

| | |
|---|---|
| Status | Draft |
| Version | 0.4.0 |
| Owner | Retrieval |
| Last updated | 2026-09-09 |

## Purpose

Embeds a query, ranks stored vectors against it, and returns the chunks that matched.

## Scope

**In scope**

- Query embedding and semantic search.
- The retrieval query contract, and the read contract it sits on.
- Ranking, score thresholds and result limits.

**Out of scope**

- How vectors were produced ([SPEC-160](SPEC-160-embedding-module.md)).
- How they are stored ([SPEC-130](SPEC-130-persistence.md)).
- Rerank, diversity and assembly under a token budget — not yet written.

## Implementation status

Brute-force cosine search is implemented over the JSON vector store. Context assembly under a
token budget is **not**; a caller gets ranked hits and decides for itself what to do with them.

## Contracts

```
IRetrievalQuery.Search(databasePath, queryText, options) -> IReadOnlyList<RetrievalHit>

RetrievalOptions(TopK = 5, MinScore = 0.0)
RetrievalHit(ChunkId, FilePath, ChunkIndex, TokenStart, TokenEnd, Score)
```

**The contract is "return the top k", not "return every vector so the caller can rank them".**
That difference is what leaves room for an implementation backed by a native nearest-neighbour
index to push ranking down into the query engine without any caller changing. A contract that
handed back every candidate would make such an implementation impossible to express.

Reads sit behind `IVectorStoreReader`, so the stored representation can change — JSON text
today, packed binary under a native extension later — without a retrieval strategy knowing.

## Requirements

### Model scoping

- **A query is scored only against vectors sharing its `model_version_id`.** Vectors from
  different embedding implementations occupy different spaces; a similarity computed across
  them is a number with no meaning.
- The scoping is enforced **in the query to the store**, not by filtering afterwards.
- Querying a model version that was never indexed **returns nothing**, rather than falling
  back to another model's vectors. An empty result is a true statement; a fallback is a
  plausible wrong answer.

### Query embedding

- The query is embedded as a **query**, not as a document. An asymmetric model treats the two
  sides differently, and getting it wrong costs relevance without raising anything.

### Ranking

- Cosine similarity, descending.
- **Ties break on chunk id**, so the ordering is total and repeatable. Without it, two chunks
  scoring identically could swap places between runs, and a comparison against a second
  backend would report a difference that is not one.
- `MinScore` drops weak matches before `TopK` is applied, so a low threshold and a high limit
  do not conspire to pad the result with noise. **It cannot currently be set to a useful
  non-zero value**, for the reason below.
- A zero-magnitude vector scores zero rather than dividing by zero.



### Ranking comes before locating

Reading is split in two. Scoring needs a chunk id and a vector; the file path and token offsets
are needed only for the chunks that actually rank.

**Resolving locations for every candidate means joining the manifests across the whole corpus to
return `TopK` rows** — metadata fetched for candidates that are about to be discarded. Measured,
that join was most of the read: 339 ms with it, 90 ms without
([SPEC-131](SPEC-131-database-options-analysis.md)).

So `IVectorStoreReader` exposes `ReadVectorsByModelVersion` for scoring and `ReadChunkLocations`
for the survivors, and the strategy resolves after it ranks. A chunk deleted between the two is
dropped rather than returned with an invented path — a hit with no source is not a result.

### A score is not yet comparable across queries

Measured over the programmable vectorizer at dimension 64
([DOCUMENTATION-ADJUSTMENTS-2026-09-09](../DOCUMENTATION-ADJUSTMENTS-2026-09-09.md)):

| | measured |
|---|---|
| cosine between **unrelated** documents | 0.7767 – 0.9342 |
| cosine for a **correct** query-to-document match | 0.7450 – 0.8933 |

The ranges overlap almost entirely — a correct match scored 0.745 while two unrelated documents
scored 0.934 against each other. The letter-frequency profile of English prose is nearly the
same whatever the prose is about, so everything is similar to everything.

**So no constant separates relevant from irrelevant, and `MinScore` cannot act as an absolute
relevance floor today.** It is left at `0.0`. Any cut-off that is going to work has to be
relative to the scores a given query actually produced.

This is a property of the vectorizer, not of cosine or of the retrieval strategy — both of which
are sound. It is the strongest argument for promoting a corpus-fitted embedder, and the reason
an absolute threshold should not be tuned before then: it would be tuned against noise.

### Locatability

A hit carries the file path and the token window, not just a score. Chunks store no text of
their own, so a hit without its source cannot be turned back into a passage — and a score with
nothing behind it is not a result.

### Dimension mismatch

If a stored vector's dimension differs from what the active model produces, retrieval **fails
loudly**. Same model version, different dimension means the stored vectors were written by a
build that disagreed with this one; scoring them would return plausible nonsense.

## Non-functional requirements

- **Reliability** — a query either ranks against a coherent vector set or fails. It never
  silently ranks against the wrong one.
- **Performance** — the baseline scan is linear in the number of stored vectors for the active
  model. Acceptable at the corpus sizes in scope; see
  [SPEC-131](SPEC-131-database-options-analysis.md) for what would change it.
- **Determinism** — the same query against the same index returns the same hits in the same
  order.

## The baseline is deliberate

`CosineRetrievalQuery` is exact and has no availability story — no per-platform binary, nothing
to detect. It is the thing a candidate backend must beat, on the same vectors and the same
metric, **agreeing with it on ranking up to ties**. Keeping it is what makes that comparison
possible.

## Open questions

- Where context assembly belongs: inside a retrieval strategy, or as a stage above it that any
  strategy feeds.
- Whether `MinScore` should be absolute, or relative to the best hit for a given query. An
  absolute floor is easy to reason about and hard to choose well — and, as measured above,
  impossible to choose at all while the active vectorizer scores everything alike.

## References

- [SPEC-130 — Persistence](SPEC-130-persistence.md)
- [SPEC-131 — Database options analysis](SPEC-131-database-options-analysis.md)
- [SPEC-160 — Embedding module](SPEC-160-embedding-module.md)
- [Reduction pipeline blueprint](../diagrams/reduction-pipeline-library-implementation-blueprint.md)
