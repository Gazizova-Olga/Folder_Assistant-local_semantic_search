# SPEC-160 — Embedding Module

| | |
|---|---|
| Status | Draft |
| Version | 0.6.0 |
| Owner | Embedding |
| Last updated | 2026-09-14 |

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

```
IVectorizer
    ModelDescriptor Descriptor { get; }
    ValueTask<IReadOnlyList<EmbeddingResult>> VectorizeAsync(texts, kind, cancellationToken)
```

- **Operations** — vectorize a batch, **asynchronously**. A batch is the primitive because most
  backends charge per call, so a caller with many texts must be able to say so; single-text
  embedding is an extension over it, not a second contract.
- **Why async** — a vectorizer may be network-bound. A local model server (`SPEC-162`) is still a
  socket, and the indexing path would otherwise block a thread per file waiting on a round-trip.
  In-process implementations complete synchronously and consume no thread, so the contract costs
  them nothing but the `ValueTask`.
- **The synchronous shim is deliberate, and it is not a shortcut.** `Vectorize(...)` wraps
  `VectorizeAsync` for the callers that stay synchronous: the retrieval query path embeds a single
  query vector per call, and making it async would ripple through the whole retrieval stack for no
  throughput gain, while the cold-fit pipeline is a batch job with nothing to overlap. It blocks
  only its own caller, never the indexing path.
- **Input** — the texts, an `EmbeddingKind`, and a cancellation token. An embedding call is
  the slowest thing on the indexing path and must be abandonable.
- **Output** — per text: the vector, the `modelVersionId` that produced it, its `dimension`,
  and the `providerType`.

### `EmbeddingKind` — which side of the pair

Every call declares whether it is embedding a **document** (an indexed passage) or a
**query**.

Several trained models are asymmetric: they are trained with an instruction prefix that
differs between the two sides, and embedding a query as though it were a document
handicaps them. The failure is silent — vectors still come back, retrieval still ranks —
so the distinction has to be in the contract rather than left to each implementation to
discover.

**A symmetric implementation may ignore it**, and should have a test pinning that it does,
so a later change of behaviour is deliberate rather than incidental.

### `ModelDescriptor` — self-description

An implementation reports its own `modelVersionId`, `providerType`, `modelName`,
`dimension` and `distanceMetric`.

**The dimension comes from here, never from configuration.** For a trained model it is
fixed by the weights, so a configured value could only ever agree or be wrong.

### `IFittableVectorizer` — corpus-dependent implementations

An implementation whose output depends on corpus statistics implements `Fit`, which returns
a serialized artifact.

The artifact is persisted per model version and restored at query time, because **a query
must be embedded with the same fit that produced the indexed vectors**. A query embedded
against a different fit is projected from a different space, and the result is not an error
— it is a plausible-looking wrong answer.

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
  database rather than inferred from configuration. Every call is observable; see Observability.

## Observability

Every embedding call emits one `EmbeddingCallTelemetry`: provider type, model version, dimension,
the number of texts in the batch, latency, a status, and — on the failure path — an error code.
It is recorded on **both** paths. A backend that has started failing is precisely what an operator
needs to see, and telemetry that only reports successes describes a system that is always healthy.

Three decisions are load-bearing.

- **Timing lives in one decorator, not in each implementation.** Measured separately, the numbers
  from two vectorizers would not be comparable, which defeats the point of being able to swap them.
- **The decorator must re-expose the inner's optional capabilities.** The cold-fit path finds a
  fittable vectorizer by type test, and startup finds the health check the same way. A wrapper that
  did not forward them would answer "no" to both while still returning good vectors — disabling
  fitting and the startup probe with no symptom but worse retrieval. Composition therefore goes
  through `EmbeddingTelemetryVectorizer.Wrap`, never the bare constructor.
- **Status separates the caller's cancellation from a deadline, and both from failure.** A call the
  caller cancelled is `Cancelled` — not a fault; an error rate that counted process shutdowns would
  tell an operator to investigate a normal exit. A call that hit a deadline is `TimedOut`, whether
  the vectorizer's own (`SPEC-162`, which reports it as a timeout) or a transport's (which reports it
  as a cancellation nobody asked for). The classification is by the caller's token before the
  exception type, because a transport's deadline and a shutdown throw the same type. Only `Failed`
  and `TimedOut` log at warning.

`latencyMs` is also stamped onto every `EmbeddingResult` the call produced. It is a property of the
batch call rather than of a text, so it is identical across a batch — one round-trip embedded all of
them — and a vector can still be asked what it cost to produce.

Status and error code are event fields only, never result fields. A materialised result is the
product of a successful call by construction, so a status on it could only ever read `Success`.

The aggregates worth watching — success rate, latency percentiles — are computed downstream from a
stream of these events rather than emitted here: an aggregate computed at the source cannot later be
sliced by anything it did not already group by.

## Open questions

- Whether an asymmetric implementation should be able to declare the prefixes it wants, or
  whether that stays each implementation's business.

## References

- [SPEC-130 — Persistence](SPEC-130-persistence.md)
- [Modular library plan](../diagrams/modular-library-plan.md)
