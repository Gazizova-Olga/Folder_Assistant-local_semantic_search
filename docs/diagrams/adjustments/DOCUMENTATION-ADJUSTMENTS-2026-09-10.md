# Documentation adjustments — 2026-09-10

What changed in `docs/diagrams/` while composition profiles and the vector-store contract suite
landed, and why. Pre-edit copies are archived at
[`docs/archive/2026-09-10/diagrams/`](../../archive/2026-09-10/diagrams/).

## `implementation-status.md` — retrieval is composed now, and still uncalled

**Was:** *"`CosineRetrievalQuery` is not registered in the composition root, and nothing outside
`Retrieval/` and the test suite references `IRetrievalQuery`."*

**Now:** `IRetrievalQuery` is registered — the active composition profile resolves one of two
implementations and either can be pulled out of the container. Nothing on the request path calls
it, so the gap is real but it is one edge, not two.

The three states this document distinguishes — live, built but unreachable, not built — are what
made the change worth recording rather than simply applying: retrieval moved between two of them
without a line of `Retrieval/` changing.

## `library-architecture.md` — two implementations, and the storage claim was two versions old

Three changes:

- **The extension-points table said each seam had one implementation.** Two of the three now have
  two. Both rows say so, and a sentence was added recording that a writer and reader are chosen
  **as a pair** by a profile, never separately — because a reader looking somewhere other than
  where the writer wrote does not fail, it reports that nothing was ever embedded.
- **"The same vectors can sit in a JSON column or a packed binary one"** described schema v2.
  Vectors have been packed little-endian `float32` since `4d8c484` (schema v3), and the live
  alternative is not a JSON column but a native `vec0` virtual table. The sentence was arguing for
  the seam using an example the repository had already abandoned.
- **The design-intent line "replaceable where replacement is real" is annotated as the one that
  held.** The separate libraries were never built (`SPEC-150`), and interchangeability arrived
  anyway — through the interfaces, exactly as that line said. Worth marking, because a design
  document is usually only revisited to correct it, and a prediction that came true is evidence
  about the method rather than about this diagram.

**What was deliberately not written into it.** Upstream's equivalent document claims `sqlite-vec`
makes indexing faster as well as retrieval. Measured here it is consistently *slower* to index —
16.3 s against 14.8 at 72,000 vectors (`SPEC-131`) — so neither figure appears. The retrieval
result reproduced and is stated; the indexing one did not and is not.

## Why these documents moved

Both adjustments records now sit in `docs/diagrams/adjustments/` rather than `docs/`, with a
[README](README.md) stating the rule. A reader who opens `docs/diagrams/` should find the record of
how those diagrams have drifted without having to know it exists — and the two inbound links, from
`README.md` and `SPEC-110`, were repointed and checked to resolve from their own directories rather
than assumed.

Specs stay revised in place and are not archived: each carries its history in its own Document
Control header, and a second copy elsewhere would give it two records that can disagree.
