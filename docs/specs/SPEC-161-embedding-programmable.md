# SPEC-161 — In-Process Embedding

| | |
|---|---|
| Status | Draft |
| Version | 0.2.0 |
| Owner | Embedding |
| Last updated | 2026-09-08 |

## Purpose

The embedding implementations that run in this process with no external service: a
deterministic baseline, and — later — a corpus-fitted model.

## Scope

**In scope**

- The deterministic baseline embedder.
- The corpus-fitted implementation and its persisted fit.

**Out of scope**

- Service-backed implementations ([SPEC-162](SPEC-162-embedding-ollama-local.md),
  [SPEC-163](SPEC-163-embedding-online.md)).
- The contract they all satisfy ([SPEC-160](SPEC-160-embedding-module.md)).

## Implementation status

The deterministic baseline is implemented and is what the composition root wires. The
corpus-fitted implementation is **not yet written**; `IFittableVectorizer` exists as a
contract with no implementation.

## The deterministic baseline

`ProgrammableEmbeddingVectorizer`, provider type `programmable`.

A character-bucket histogram, L2-normalized: each character increments a bucket chosen by
its code point modulo the dimension, and the vector is scaled to unit length.

### What it is for

Not retrieval. It exists so that:

- the storage and retrieval paths are testable before there is anything real to embed with;
- the pipeline has an end-to-end floor that runs with no model, no network and no
  non-determinism;
- a real implementation has a measurable thing to beat.

### What it cannot do

**It carries no semantics, and the clearest statement of that is that any anagram embeds
identically** — `"dog"` and `"god"` produce the same vector, because the histogram is
position-insensitive.

This is asserted in its tests rather than only described here. If that assertion ever
fails, the baseline has stopped being the baseline, and any comparison against it has
stopped meaning what it used to.

### Behaviour

- **Symmetric.** It ignores `EmbeddingKind`; document and query embed identically. Pinned by
  a test, so an implementation that later wants to differ has to say so.
- **Deterministic.** The same text under the same model version always gives the same
  vector. No clock, no randomness, no corpus dependency.
- **Dimension is configured**, and is the one case where that is legitimate: there are no
  weights to fix it. Any positive dimension is valid; zero or negative is refused at
  construction rather than producing an empty vector.
- **Whitespace-only or empty text** embeds to the zero vector rather than failing. It is not
  a useful vector, but it is a defined one, and the caller has a chunk to account for.
- **Cancellation is observed between texts** in a batch.

## The corpus-fitted implementation

Not yet written. Recorded here so the requirements are not invented afterwards:

- It implements `IFittableVectorizer`. Vectorizing before fitting is an error, not a
  silently poor vector.
- The fit is serialized and persisted with the vectors it produced, in the same transaction.
  A fit that disagrees with the stored vectors corrupts every query embedded against it.
- The analyzer configuration is part of the artifact. A query analyzed differently from the
  corpus is projected from a different feature space, and fails silently rather than loudly.
- It must **measurably out-rank the baseline** on a labelled set. An implementation that
  ranks no better than a character histogram is not doing what it exists to do, and that is
  a test rather than a judgement.

## Determinism, stated precisely

Determinism here means **stable for a given `(input, kind, model_version_id)`** — not stable
across model versions, and not stable across fits. A corpus-fitted implementation
necessarily produces different vectors after refitting; that is why the fit is part of the
model version's identity rather than a property of a run.

## Open questions

- Whether the baseline should stay selectable once a fitted implementation exists, or become
  test-only. It is currently the only thing that guarantees an offline default.

## References

- [SPEC-160 — Embedding module](SPEC-160-embedding-module.md)
- [SPEC-130 — Persistence](SPEC-130-persistence.md)
