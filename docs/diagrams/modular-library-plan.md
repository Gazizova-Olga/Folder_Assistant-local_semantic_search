# Plan for Modular, Interchangeable Libraries

How the system gets from "one folder, one embedder" to "several embedding implementations
that can be swapped and compared", without a rewrite at the end.

> Renamed from an earlier internal filename that carried a delivery-methodology label. The
> content is the plan; the label was not part of it.

## Purpose

Interchangeability is easy to claim and hard to keep. This plan sets out what has to be
true for it to be real, and in what order those things arrive.

## What "done" means

- More than one embedding implementation exists and passes the same contract suite.
- Switching between them is a composition change, not an edit spread across callers.
- Their vectors coexist in one database, so they can be compared against one corpus.
- A benchmark exists that says which one is better, on evidence rather than preference.
- Nothing on the default path requires a network service.

## Baseline

The starting point is a working single-implementation pipeline: scan, chunk, embed with a
deterministic placeholder, store, retrieve by cosine similarity. That baseline is not
scaffolding to be thrown away — it is the floor a real implementation has to beat, and the
thing that keeps the storage and retrieval paths testable while the embedder changes.

## The interchangeability model

Three seams, and nothing else pretending to be one:

| Seam | Question it answers |
|---|---|
| `IVectorizer` | How is text turned into a vector? |
| `IVectorStoreWriter` / `IVectorStoreReader` | How is a vector stored and read back? |
| `IRetrievalQuery` | How are candidates ranked? |

Two rules keep these honest:

- **The vectorizer reports its own dimension.** For a trained model the dimension is fixed
  by the weights, so configuration must not be able to contradict it.
- **A corpus-fitted implementation carries its fit.** The fit that produced the indexed
  vectors is the fit a query must be embedded with. It is persisted alongside them, in the
  same transaction, and restored at query time.

### Embedding implementations

| Spec | Implementation | Runs |
|---|---|---|
| [SPEC-161](../specs/SPEC-161-embedding-programmable.md) | Deterministic baseline; corpus-fitted model | In process |
| [SPEC-162](../specs/SPEC-162-embedding-ollama-local.md) | Local Ollama server | On the machine |
| [SPEC-163](../specs/SPEC-163-embedding-online.md) | Hosted API | Over the network |

## Contract rules

- A contract change is a version change, recorded in its spec.
- An implementation may not widen a contract for its own convenience; if it needs more,
  the contract needs revisiting.
- Every implementation passes the same suite. A test that only one can pass is testing the
  implementation, not the contract.

## Delivery phases

**Phase 0 — baseline and definitions.** The pipeline works end to end with the placeholder.
The specs exist, even where empty. A warning gate is in place.

**Phase 1 — contracts and registry.** The seams above are extracted. The model registry
records which implementation produced which vectors, and enforces one active for write.

**Phase 2 — implementations.** The corpus-fitted model, then the service-backed ones. Each
lands with its own tests and its own spec.

**Phase 3 — quality and cost.** Retrieval quality measured rather than assumed. Bounds on
calls that can hang. Telemetry on the paths that fail silently.

**Phase 4 — rollout.** A default is changed only on benchmark evidence, and the previous
implementation stays selectable.

## Test strategy

- **Contract suite** — every implementation, same assertions.
- **Determinism** — the same input under the same model version gives the same vector.
- **Coexistence** — two model versions indexed against one corpus, neither disturbing the
  other, exactly one active for write.
- **Quality regression** — a fitted implementation must out-rank the placeholder on a
  labelled set. If it does not, the fitting is not doing anything.
- **Offline** — the default path with no network reachable.

## Observability

Each embedding call and each retrieval records what it did and how long it took, split by
whether it was indexing or answering. A background batch and a query a user is waiting on
share no latency profile, and averaging them together describes neither.

## Risks

| Risk | Mitigation |
|---|---|
| A fitted model silently degrades to lexical matching | Assert the property that distinguishes them, not just that it runs |
| Vectors compared across model versions | Scope every query to one `model_version_id` |
| An unbounded call to a local service hangs indexing forever | Bound the call; a file that never returns must fail rather than wait |
| Interfaces multiply past their usefulness | A seam needs a second real implementation, not a hypothetical one |

## Implementation status

**Phase 0 is under way.** Startup persistence bootstrap exists: the application resolves the
folder-scoped database, creates it when missing, and ensures the baseline manifest, chunk,
vector and model-registry schema on every run.

**Phase 1 has started.** `IVectorizer` is extracted and the model registry records which
implementation produced which vectors, with exactly one active for write. The deterministic
baseline is the only implementation so far.

## References

- [Library architecture](library-architecture.md)
- [SPEC-000 — System concept](../specs/SPEC-000-system-concept.md)

