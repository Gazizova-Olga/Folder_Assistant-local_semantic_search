# Documentation adjustments — 2026-09-09

The whole documentation set re-checked against the code, and every adjustment recorded here.

The specs governing the indexing and embedding track (`SPEC-110`, `SPEC-120`, `SPEC-130`,
`SPEC-160`, `SPEC-161`) were accurate — they have been revised alongside the code they govern,
one step at a time, which is what the spec-alignment gate is for. **The drift was concentrated
in the documents that gate does not cover**: the README, and the parts of `CLAUDE.md` that
describe how to work rather than what is built.

---

## Corrected

### `README.md` — described a repository that no longer exists

The Status section still said the repository "currently holds the solution skeleton: project
layout, centralised package management, analyzer configuration and editor settings", and that
"the application itself is built up from here". By this commit there is a scanner, a chunker,
two embedding implementations behind one seam, a folder-scoped SQLite store, cosine retrieval,
a background indexing service, a filesystem watcher, and 154 tests.

Also corrected:

- **Layout** listed three entries and omitted `docs/` entirely.
- **Nothing said that running the application writes to the folder it is pointed at.** It
  creates a `.folderassistant/` directory holding a SQLite database, and starts a filesystem
  watcher over the folder. Someone running `dotnet run` to see what happens deserves to know
  that before they run it, not after.

### `CLAUDE.md` — one stale sentence

"Once the solution lands, from the repository root" introduced the commands section. It landed.

The rest of that document is deliberately written as target architecture and says so, so it is
not drift when it describes something not yet built. That framing is doing real work and is
kept.

### `SPEC-150` — records a decision that had already been taken

Everything lives in one assembly. There is no `Shared.Abstractions` library, and the contracts
that cross module boundaries (`IVectorizer`, `IVectorStoreReader`/`Writer`, `IRetrievalQuery`,
`IIndexState`) sit beside the code that uses them. That was decided by building it that way; it
was not written down anywhere. Now it is, with the reasoning, so that a future split is a
decision rather than a discovery.

### `SPEC-110` — `MinScore` is not an absolute threshold

See the measurement below. The spec described `MinScore` as a relevance floor without saying
what a score means, which is not true of the vectorizer that is actually wired up.

---

## Measured, not assumed: the programmable vectorizer under cosine

`SPEC-161` says the baseline embedder carries no semantics. That is stated as a property of the
construction — a character-bucket histogram cannot encode word order or meaning — but what it
does to *scores* had not been measured. It has now, over a five-document corpus of unrelated
English prose, at dimension 64.

| | measured |
|---|---|
| cosine between **unrelated** documents | 0.7767 – 0.9342 |
| cosine for a **correct** query-to-document match | 0.7450 – 0.8933 |

**The two ranges overlap almost entirely.** A correct match scored 0.745 while two unrelated
documents scored 0.934 against each other. Everything is similar to everything, because the
letter-frequency profile of English prose is nearly the same whatever the prose is about.

**The consequence: `MinScore` cannot serve as an absolute relevance threshold today.** No
constant separates "relevant" from "irrelevant" when the two distributions sit on top of each
other. Any cut-off has to be relative to the scores a given query actually produced.

**What was *not* found, stated because it is the more interesting half.** Ranking was mostly
correct: the expected document came first for 7 of 8 queries. That is not evidence the embedder
works. Five documents is a small corpus, and with distinct enough vocabulary a histogram can
order them correctly by accident while the score *margins* remain meaningless — which is exactly
what the overlap above shows. **The ranking result is reported because it was measured, not
because it supports the conclusion.**

---

## Not fixed, because documentation cannot fix them

### Retrieval is built, tested, and unreachable

`CosineRetrievalQuery` has no runtime caller. It is not registered in the composition root and
nothing outside `Retrieval/` and the test suite references `IRetrievalQuery`. So indexing writes
vectors that nothing ever reads in the running application: **the RAG loop is open.**

Everything on the write side is real — scan, chunk, embed, store, delta-handling, the readiness
gate. The query side exists and is tested. They are simply not connected, and no amount of
accurate documentation closes that.

This also means the measurement above and the promotion of a corpus-fitted embedder are one
piece of work rather than two: there is no point tuning what a running system cannot reach.

### `appsettings.json` omits the sections most worth configuring

`Persistence` and `Indexing` are absent. They run entirely on the defaults in `AgentConfig`,
which is legitimate — but someone reading the file to learn what is configurable would conclude
the analyzed folder and the indexing behaviour are not. The README now carries an example
instead; whether the shipped file should is left open.

---

## Checked and found accurate

Recorded so the next audit does not re-derive them.

- `SPEC-161`'s description of the histogram — "each character increments a bucket chosen by
  its code point" — matches the implementation. It does not claim character codes are summed.
- No planning document claims to be planning-only or unimplemented.
- The provider's model property is named `DeploymentName` throughout, in code and in
  `appsettings.json`.
- `SPEC-120`, `SPEC-130` and `SPEC-100` match the code; each was revised in the commit that
  changed the behaviour it describes.
