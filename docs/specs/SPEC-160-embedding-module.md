# SPEC-160 — Embedding Module

| | |
|---|---|
| Status | Draft |
| Version | 0.2.0 |
| Owner | Embedding |
| Last updated | 2026-06-26 |

## Purpose

Defines what every embedding implementation must satisfy, so that one can be exchanged for
another without the rest of the system noticing.

## Scope

**In scope**

- The vectorizer contract common to all implementations.
- Model identity, vector dimension and how they are recorded.
- Determinism and coexistence requirements.

**Out of scope**

- Any one implementation's internals — see [SPEC-161](SPEC-161-embedding-programmable.md),
  [SPEC-162](SPEC-162-embedding-ollama-local.md), [SPEC-163](SPEC-163-embedding-online.md).

## Contract

- **Operations** — vectorize a single text, and vectorize a batch. A batch is the primitive:
  most backends charge per call, so a caller with many texts must be able to say so.
- **Input** — the text, plus a cancellation token. An embedding call is the slowest thing on
  the indexing path and must be abandonable.
- **Output** — the vector, the `modelVersionId` that produced it, its `dimension`, and the
  `providerType`.

## Requirements

### Identity and dimension

- An implementation **reports its own dimension**. For a trained model the dimension is
  fixed by the weights, so configuration must not be in a position to contradict it.
- Every vector carries the model version that produced it. A vector whose provenance is
  unknown cannot be safely compared with anything.

### Storage

- Output must be storable in the shared schema, keyed
  `(chunk_id, model_version_id)` ([SPEC-130](SPEC-130-persistence.md)).
- **Implementations coexist.** Indexing a folder with a second implementation must not
  overwrite or invalidate the first one's vectors — that coexistence is what makes
  comparing them against one corpus possible at all.

### Retrieval

- **A query vector is compared only against vectors of the same `modelVersionId`.** Vectors
  from different implementations occupy different spaces; a similarity computed across them
  is a number with no meaning.
- Querying a model version that was never indexed returns nothing, rather than silently
  falling back to another implementation's vectors.

### Determinism

- The same input, under the same model version, produces the same vector. Where an
  implementation depends on corpus statistics, "the same model version" includes the fit —
  which is therefore part of what gets persisted, not a detail of one run.

## Non-functional requirements

- **Reliability** — a call that cannot complete fails; it does not return a vector that is
  quietly wrong.
- **Performance** — batching is available to any caller that has more than one text.
- **Operability** — which implementation is active, and its dimension, are recorded in the
  database rather than inferred from configuration.

## Open questions

- Whether the contract should distinguish the two sides of a retrieval pair. Some trained
  models are asymmetric and expect different treatment for an indexed passage than for a
  search query.

## References

- [SPEC-130 — Persistence](SPEC-130-persistence.md)
- [Modular library plan](../diagrams/modular-library-plan.md)
