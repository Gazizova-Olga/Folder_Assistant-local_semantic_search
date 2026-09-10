# SPEC-110 — Retrieval

| | |
|---|---|
| Status | Draft |
| Version | 0.9.0 |
| Owner | Retrieval |
| Last updated | 2026-09-11 |

## Purpose

Embeds a query, ranks stored vectors against it, and returns the chunks that matched.

## Scope

**In scope**

- Query embedding and semantic search.
- The retrieval query contract, and the read contract it sits on.
- Ranking, score thresholds and result limits.
- Rerank, diversity, and assembly under a token budget — as a stage **over** the retrieval
  contract's output, not inside it.

**Out of scope**

- How vectors were produced ([SPEC-160](SPEC-160-embedding-module.md)).
- How they are stored ([SPEC-130](SPEC-130-persistence.md)).

## Implementation status

Both retrieval backends are implemented, and context assembly now exists as a separate stage
(`IContextReduction`, default `TokenBudgetContextReducer`).

**Nothing calls the reducer yet.** It is composed and resolvable, in the same position retrieval
itself occupied before the composition root registered it: the stage a consumer will run, with no
consumer built. A semantic-search tool is what will use it, and that belongs to the agent work.

Per-call telemetry wraps the composed query and is exported at `GET /metrics` (see Observability).
It records what searches do, so until something searches it has nothing to record — the instrument
is in place ahead of the traffic, not measuring traffic that exists.

The low-confidence screen (`RelevanceFloor`) is likewise built and uncalled, and **off by default**
for a measured reason given below. Only the screen itself is here: the requirement it comes from
also asked that the caller size its own result count and that the model be told to judge question
scope before searching, and both of those live in a tool description and a system prompt — surfaces
this repository does not have until the agent lands.

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

That is no longer a hypothetical justification for a shape. `SqliteVecRetrievalQuery` is that
implementation — it ranks inside the query engine through `sqlite-vec`, satisfies this contract
unchanged, and is 15–26x faster on retrieval than the brute-force baseline (`SPEC-131`). It is a
comparison candidate rather than the default, and it is selected explicitly.

**Model scoping is structural in that backend, not checked.** A `vec0` table fixes its vector
dimension at creation, so each model version gets its own table. Searching one cannot reach
another's vectors, which makes "never score across embedding spaces" impossible to violate rather
than merely forbidden.

Reads sit behind `IVectorStoreReader`, so the stored representation can change — JSON text
yesterday, packed binary today, a native extension's virtual table alongside it — without a
retrieval strategy knowing. One read is on that interface for a reason worth stating: which files
already have vectors can only be answered by the store that holds them. A backend keeping vectors
in a virtual table leaves `chunk_vector` empty, so a lookup written against that table would
report that nothing has ever been embedded — and every file would be re-embedded on every run,
silently, with the index still looking correct.

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
([DOCUMENTATION-ADJUSTMENTS-2026-09-09](../diagrams/adjustments/DOCUMENTATION-ADJUSTMENTS-2026-09-09.md)):

| | measured |
|---|---|
| cosine between **unrelated** documents | 0.7767 – 0.9342 |
| cosine for a **correct** query-to-document match | 0.7450 – 0.8933 |

The ranges overlap almost entirely — a correct match scored 0.745 while two unrelated documents
scored 0.934 against each other. The letter-frequency profile of English prose is nearly the
same whatever the prose is about, so everything is similar to everything.

**So no constant separates relevant from irrelevant under this vectorizer, and `MinScore` cannot
act as an absolute relevance floor while it is the one composed.** It is left at `0.0`.

This is a property of the vectorizer, not of cosine or of the retrieval strategy — both of which
are sound. It is the strongest argument for promoting a real embedder, and the reason an absolute
threshold should not be tuned against this one: it would be tuned against noise.

**And with a real embedder it does separate**, measured — see "Can a score floor tell a good
question from a bad one?" below. An earlier version of this section concluded from the
placeholder's numbers that *any* workable cut-off "has to be relative to the scores a given query
actually produced". That inference was too strong: it generalised a property of one deliberately
semantics-free embedder into a property of absolute thresholds. The measurement above stands; the
conclusion drawn from it has been narrowed to the vectorizer it was taken on.

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

## Rerank and budget policy

Raw top-k is not context. The k best passages by score are often near-duplicates — the same
paragraph chunked twice, or one idea stated twice in a file — so handing all of them to a model
spends the budget restating one thing while the answer sits in the passage that ranked ninth.

- **Relevance is blended**: `(1 - lexicalWeight) * semantic + lexicalWeight * coverage`, where
  coverage is the fraction of the query's distinct terms the passage contains. `lexicalWeight`
  defaults to `0.3`, so semantics lead but a passage carrying the exact keyword can overtake a
  near-tied paraphrase.
- **Selection is greedy MMR**, with diversity measured as Jaccard over passage word sets rather
  than over vectors. That keeps vectors out of the reduction contract entirely, and costs nothing
  on the query path.
- **Selection stops at the token budget**, and at `MaxPassages`, whichever binds first.
- **The best passage is always returned**, even when it alone exceeds the budget. Some context
  beats none, and an empty result is indistinguishable from having found nothing.
- **A passage skipped for size does not end the scan.** A smaller one after it can still fit;
  stopping at the first overflow would make the result depend on candidate order.

The reported score is the blended relevance, not the raw retrieval score — reporting the latter
would explain a different ordering than the one produced.

**Deferred, deliberately:** per-call budget configuration (the defaults are code constants),
vector-based MMR, and a stopword list. The lexical signal is coarse and carries a minority share
of the ranking, so stopwords would be tuning a weight that mostly is not deciding anything.

## The low-confidence short circuit

A set of weak passages is worse than no passages. It costs context tokens, and it invites an answer
built on text that does not address the question — which reads exactly like an answer built on text
that does. `RelevanceFloor.Screen` therefore refuses a whole candidate set when even its **best**
match falls below a floor.

Three properties are deliberate.

- **It screens on the best score, not on each candidate.** Per-candidate filtering already exists as
  `RetrievalOptions.MinScore`, applied by both backends while ranking. Thinning a set and refusing
  one are different decisions, and the best match is the one that says whether the corpus has
  anything to say at all.
- **It sits above the reducer, and has to.** `IContextReduction` promises to return the best
  candidate it was given even when that candidate alone exceeds the budget, precisely so an empty
  result cannot be confused with having found nothing. A stage that must sometimes return nothing
  cannot live inside a stage that must never return nothing.
- **Refused and empty stay distinguishable.** An empty set is not low confidence — reporting it as
  such would tell a caller to rephrase a perfectly good question and hide that there was nothing to
  search. The best score is reported either way, because a set turned away at `0.39` and one turned
  away at `0.01` describe different corpora.

**The default is `RelevanceFloor.Off`.** The floor that applies is a property of the embedding
model, and the measurement below found that the default profile's scores cannot support one at all.
A shipped non-zero default would claim a filtering power those scores do not have, which is the
same reason `MinScore` defaults to `0.0`.

## Can a score floor tell a good question from a bad one?

A short circuit that refuses to answer from weak matches needs a threshold, and a threshold is
only worth having if the scores it cuts actually separate questions the corpus can answer from
questions it cannot. That is measurable, and it had not been measured.

`RelevanceFloorBenchmark` (opt-in, `RELEVANCE_FLOOR_BENCH=1`) puts five documents on unrelated
everyday subjects in a folder, then asks five questions the corpus answers and five from domains
it contains nothing on — worded to avoid quoting the documents, so a match is meaning rather than
shared vocabulary. It records the top-hit score per query. Both embedders see the identical corpus
and the identical queries, because the point is to tell "a floor cannot work" apart from "this
embedder scores everything alike".

| | answerable | unanswerable | separation |
|---|---|---|---|
| `programmable-blob` (the default profile) | 0.8464 – 0.9004 | 0.8033 – **0.9220** | **−0.0756 — overlapping** |
| `qwen3-embedding:0.6b` (`ollama-blob`) | **0.5040** – 0.7215 | 0.1209 – **0.2520** | **+0.2520 — separable** |

**With a real embedder the premise holds, cleanly.** Every answerable question outscores every
unanswerable one, and any floor in `(0.2520, 0.5040]` divides them.

**With the placeholder embedder no floor can work at all.** The populations overlap, and the
highest-scoring query of all ten is one the corpus cannot answer. This is the same property
recorded above and in `SPEC-161`, reproduced here as the control: a harness that did not show it
would be measuring itself.

**A floor of `0.2` — the obvious round number — is on the correct side but under-selective.** It
rejects 4 of 5 unanswerable questions and none of the answerable ones, missing one at `0.2520`. It
sits essentially at the top of the unanswerable distribution rather than between the two, which
makes it a value that happens to work rather than one chosen from a measurement.

**What this does not establish.** Ten queries, one small corpus, one model. Two runs are
bit-identical, so the band edges are not model noise — but they rest on five samples per bucket,
and the width of the separation is the robust part, not the exact edges. The honest conclusion is
that a floor is *possible* with a real embedder and *impossible* with the placeholder, not that
any particular constant is the right one to ship.

## Observability

The unit is the call, not the hit. A search ranks its whole candidate set in one pass and succeeds
or fails as a whole, so each `IRetrievalQuery.Search` emits exactly one `RetrievalCallTelemetry`
through the `IRetrievalTelemetry` sink: backend, requested top-k, result count, latency, status,
and — when the status is not `Success` — the type name of what went wrong.

Recording lives in a decorator over the composed query (`RetrievalTelemetryQuery.Wrap`), not inside
either backend. Both implementations exist to be compared against each other on latency; measured
separately they would be two instruments as much as two backends, and the comparison would carry
the difference between the instruments.

**Three of the four statuses are deliberately not `Failed`.**

- `NotReady` — the index was not queryable yet. Every process reports this until its first pass
  finishes, so it is the one status a dashboard should expect and not act on. It logs at
  information, not warning, for the same reason: an alert keyed on warning volume would fire on
  ordinary startup traffic.
- `Failed` on a *failed build* — the same `IndexNotReadyException` type carries both conditions,
  and only the still-building one is benign. A build that failed is usually a backend that stopped
  answering, and filing it under `NotReady` would describe a dead dependency as a condition that
  clears on its own. `IndexNotReadyException.IsBuildFailure`, set by `RetrievalGuard` at the throw
  site, is what separates them; the `errorCode` names the underlying error rather than the refusal
  wrapped around it.
- `TimedOut` — a search embeds its query text before it can rank anything, so an embedding backend
  that stops answering ends the *search*. That is a fault outside the backend being measured, and
  charging it to whichever backend happened to be composed would report a regression in the wrong
  place.

**Deliberately not recorded:** candidate-pool size and the active model version. Neither crosses
the `IRetrievalQuery` boundary, and a decorator that reported them would be reporting what it
assumed rather than what it observed.

The default sink writes both a structured log line and a `Meter`, exported at `GET /metrics` for a
Prometheus scrape. Two channels rather than one: the log line is what someone reads when a single
query behaved oddly, and the meter is what a dashboard reads — and it keeps working when the log
level is raised to suppress routine successes, which is the first thing done to a per-call log.
`docs/observability-retrieval.md` is the operator reference.

**Known gap.** The timeout classification currently recognises cancellation only, which is what a
stalled embed actually produces here — the HTTP client's own deadline is the thing that gives up.
Once the embed call carries a deadline of its own and reports it as such, this classification has
to learn that second shape, or a bounded embed will be recorded as a plain failure.

## Open questions

- ~~Where context assembly belongs: inside a retrieval strategy, or as a stage above it.~~
  **Resolved: a stage above it.** Ranking and budgeting answer different questions — what matches,
  and what is worth spending the context on — and keeping them apart means the retrieval contract
  stays a pure top-k similarity search. A native k-NN backend needs no rerank logic pushed into it,
  and the reducer works the same whichever backend produced the candidates.
- Whether `MinScore` should be absolute, or relative to the best hit for a given query.
  **Partly answered (0.9.0), by measurement:** an absolute floor is choosable and effective with a
  real embedder — `(0.2520, 0.5040]` separates the two populations cleanly under
  `qwen3-embedding:0.6b` — and remains impossible under the placeholder, whose scores overlap. So
  the question was never "absolute or relative" in general; it was "which embedder is composed".
  What stays open is the *shipped default*: ten queries over one corpus is not enough to fix a
  constant that fires on real questions, and `RelevanceFloor.Off` is the honest default until there
  is more evidence or a per-profile value.
- Whether the floor should be configurable per profile rather than per deployment. The measurement
  says the right value is a property of the embedding model, and the composition profile already
  knows which model it built — but wiring a retrieval constant into `ModuleSet` is a larger change
  than this evidence justifies.

## References

- [SPEC-130 — Persistence](SPEC-130-persistence.md)
- [SPEC-131 — Database options analysis](SPEC-131-database-options-analysis.md)
- [SPEC-160 — Embedding module](SPEC-160-embedding-module.md)
- [Reduction pipeline blueprint](../diagrams/reduction-pipeline-library-implementation-blueprint.md)
