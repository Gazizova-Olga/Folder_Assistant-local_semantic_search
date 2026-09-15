# SPEC-161 — In-Process Embedding

| | |
|---|---|
| Status | Draft |
| Version | 0.5.0 |
| Owner | Embedding |
| Last updated | 2026-09-15 |

## Purpose

The embedding implementations that run in this process with no external service: a
deterministic baseline and a corpus-fitted model.

## Scope

**In scope**

- The deterministic baseline embedder.
- The corpus-fitted implementation and its persisted fit.

**Out of scope**

- Service-backed implementations ([SPEC-162](SPEC-162-embedding-ollama-local.md),
  [SPEC-163](SPEC-163-embedding-online.md)).
- The contract they all satisfy ([SPEC-160](SPEC-160-embedding-module.md)).

## Implementation status

Both implementations are built and tested. The deterministic baseline is what the composition
root wires; the corpus-fitted one is selected explicitly and is not a default yet — promoting
it waits on benchmark evidence against a labelled set.

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

`LsaEmbeddingVectorizer`, provider type `programmable-lsa`.

TF-IDF over an analyzer that splits identifiers and stems, reduced by a truncated latent
semantic decomposition. Pure managed arithmetic, so the offline property holds.

### What it buys

Weak synonymy. On a corpus where *car* and *automobile* never co-occur but share every
context, a query for one retrieves the other's documents. Nothing lexical can do that, and
the baseline is asserted **not** to — which is what makes the comparison mean something
rather than merely showing that the fitted model runs.

### What it actually bought, measured (2026-09-12)

The synonymy claim above rested on a nine-document fixture, which can show that the mechanism works and
cannot show that it is worth anything. `SemanticSearchBenchmark` now measures it on three hundred
documents built so that nothing but meaning separates one topic from another — several differently worded
documents per topic over identical neutral filler.

| | placeholder | fitted (k = 32) | pretrained |
|---|---:|---:|---:|
| MAP | 0.031 | **0.401** | 0.675 |
| P@1 | 7% | 45% | 82% |

**"Weak synonymy" turns out to be the right phrase for it.** Thirteen times the placeholder's mean average
precision, so the fit finds something real; a little over half the pretrained model's, so it does not
replace one. Both halves of that matter — a result near zero would have said the reduction buys nothing on
a realistic corpus, and a result near the pretrained model's would have said a corpus-fitted embedder makes
the network dependency unnecessary. Neither is what happened.

It cost 5.5 ms per chunk to index against the placeholder's 0.7 ms and the pretrained model's 386 ms, so
on cost it sits nearer the floor than the ceiling. This still does not promote it: one corpus, of
one shape, against one choice of k.

### Two properties that are load-bearing

**1. `k` must sit well below the corpus rank, and above one.** The compression *is* the
mechanism. Measured on a nine-document corpus of rank 8:

| k | car / automobile | car / bread |
|---|---|---|
| 1 | 1.000 | 1.000 — degenerate, one concept, discriminates nothing |
| 2 | 0.988 | 0.185 |
| 3 | 0.871 | 0.185 |
| 4 | −0.514 | 0.126 — collapsed |
| 8 | −0.577 | 0.108 |

Both ends fail **silently**: too low and everything resembles everything, too high and the
synonymy disappears. No error is raised in either case; retrieval simply returns worse
answers. A caller choosing `k` has no feedback saying it chose badly.

**2. The projection scales by `1/√λ`, not `1/λ`.** Dividing by λ cancels Σ out of the
transform, weighting a weak noise concept exactly as heavily as the dominant one. Measured
at k = 3: **0.871 with `1/√λ`, 0.701 with `1/λ`**. Note how narrow that gap is — at k = 2 the
two are 0.988 and 0.981, effectively indistinguishable. The error is only visible in a band,
so a test that happened to pick a different `k` would not catch it at all.

The reduction is computed from the document-space Gram matrix rather than a full SVD, which
would materialise a vocabulary-squared factor.

### Requirements

- Vectorizing before fitting **throws**. It is not a silently poor vector.
- The fit is serialized and persisted **in the same transaction as the vectors it produced**.
  An artifact that disagrees with the stored vectors corrupts every query embedded against it
  and nothing reports that.
- The analyzer configuration is part of the artifact, so a query cannot be analyzed
  differently from the corpus it will be compared against.
- The descriptor reports the rank **actually fitted**, which may be below the requested
  target when the corpus cannot support it. Reporting the real number is what keeps the
  registry and the stored vectors agreeing.
- Text made entirely of out-of-vocabulary terms embeds to zero, scoring zero against
  everything — the honest answer for a query the corpus has no words for.
- It must **measurably out-rank the baseline** on a labelled set before it becomes a default.

### What it is not

It carries no pretrained weights, so on a paraphrase query it stays below a trained
transformer embedding by construction. What it offers is semantics fitted to *this* folder,
with nothing to download and nothing to reach.

## Determinism, stated precisely

Determinism here means **stable for a given `(input, kind, model_version_id)`** — not stable
across model versions, and not stable across fits. A corpus-fitted implementation
necessarily produces different vectors after refitting; that is why the fit is part of the
model version's identity rather than a property of a run.

## The corpus-fitted embedder as the default (2026-09-15)

`lsa-*` is the default profile family, so the folders it fits include the smallest ones. Two rules
follow, both implemented in the trainer and asserted by `LsaTinyCorpusTests`:

- **A corpus below the pruning floor keeps its whole vocabulary.** A term must normally appear in two
  chunks to survive pruning; one chunk, or a few sharing no term, would leave nothing and fail the
  index of a folder that has text in it. When pruning leaves no terms the vocabulary is kept whole and
  the fit is lexical rather than latent — which is what a corpus that small can support, and is a fit
  rather than a failure. The rank is still clamped to what the corpus can carry.
- **An empty corpus still refuses to fit.** There is nothing to fit on; the whole-folder pass fails,
  the index reports `Failed` with the reason, and the pass is retried on its interval until the folder
  has text.

**The fit is taken once and kept** (`SPEC-120`, fit reuse). A folder that starts with two files and
grows to two hundred keeps the two-file fit until the metadata folder is deleted, and nothing detects
the drift. That cost was accepted when `lsa-*` was one profile among six; as the default it is the
first-run experience, and a refit policy — refit when the corpus has grown past some multiple of the
size it was fitted on — is the next thing this spec owes. Recorded as an open question below, and in
the plan.

**The model version is its own.** `lsa-*` persists vectors and the fit under
`Indexing:LsaModelVersionId` (`lsa-v1`), not the programmable profile's id: the two embed into
unrelated spaces, vectors are compared only within one model version, and a shared id would have let a
profile switch mix them.

## Open questions

- **A refit policy for the corpus-fitted embedder.** The fit is stale the moment the folder grows
  past what it was fitted on, and it is now the default. Candidate signal: chunk count at fit time,
  stored with the artifact; refit when the current count exceeds it by a factor. The refit invalidates
  every vector of the model version, so it is a whole-folder pass, not an incremental one.

- How `k` should be chosen for a real corpus. The band is real and both edges fail silently;
  nothing currently derives it from the corpus, and a caller picking badly gets no signal. The
  measurement above used k = 32 against 300 documents and scored MAP 0.401 — one point, deliberately
  chosen well below the corpus rank. It is evidence that a reasonable k works, not a rule for picking one.
- Whether the baseline should stay selectable now that a fitted implementation exists, or become
  test-only. It is currently the only thing that guarantees an offline default.

## References

- [SPEC-160 — Embedding module](SPEC-160-embedding-module.md)
- [SPEC-130 — Persistence](SPEC-130-persistence.md)
