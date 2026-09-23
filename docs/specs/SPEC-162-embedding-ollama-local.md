# SPEC-162-embedding-ollama-local — Local Embedding via Ollama

| | |
|---|---|
| Status | Draft — core implemented and live-verified; the default profile |
| Version | 0.9.0 |
| Owner | Embedding |
| Last updated | 2026-09-24 |

## Purpose

An embedding implementation backed by a locally running Ollama server, so the system can retrieve
by **meaning** rather than by surface similarity without any text leaving the machine.

This is the first implementation here that is a real model. The placeholder is a character
histogram and carries no semantics at all; the corpus-fitted LSA vectorizer captures weak synonymy
from the corpus it was fitted on and nothing beyond it. Neither can connect a question to an answer
that shares none of its words, which is the ordinary case for a person asking about their own files.

## Scope

**In scope**

- Client configuration and model selection.
- The query/document asymmetry the model requires.
- Vector width validation against the composed model version.

**Out of scope**

- Cloud-hosted embedding (`SPEC-163`).
- Pulling or managing the model. The operator runs Ollama and pulls the model; this reads it.

## Requirements

### The model, and the two properties that are load-bearing

The target model is `qwen3-embedding:0.6b`, served over Ollama's OpenAI-compatible endpoint.

- **It is asymmetric.** The query side takes the model's own retrieval instruction prefix; documents
  are embedded verbatim. This maps exactly onto `EmbeddingKind`, which existed before this
  implementation and is why no new concept was needed. Getting it wrong does not fail — it quietly
  degrades ranking — so the prefix is a constant in the implementation rather than a setting.
- **Its width is fixed by the weights.** 1024 for this model. The configured dimension therefore
  exists to be *checked against*, never to choose: a model emitting a different width fails the call
  rather than writing a vector into a `(chunk_id, model_version_id)` space that assumes one width.
  A wrong-width vector would not be detected later; every query under that version would simply be
  compared against something it does not match.

### Contract and transport

`OllamaEmbeddingVectorizer` implements `IVectorizer` and **not** `IFittableVectorizer` — the model
is pretrained, so there is no corpus fit to compute, persist, or restore before a query can be
embedded. That is a real difference from the LSA path, where a query embedded by an unfitted
instance lands in the wrong space.

The transport is `Microsoft.Extensions.AI`'s embedding generator over the OpenAI client, pointed at
the local endpoint. **This is a dependency the project did not previously have**, and it is recorded
rather than glossed: nothing else in the tree speaks to a model server. Ollama authenticates
nothing, so a placeholder credential is passed to satisfy the client.

Embedding is asynchronous (`SPEC-160` 0.4.0). A local model server is still a socket, and the
indexing path would otherwise block a thread per file waiting on a round-trip.

### A bounded call

**Every call has a deadline, whatever its batch size, and a call that reaches it fails as a
timeout.** Without one, a server that stopped answering held a delivery in flight forever with its
attempts still at zero — retry, backoff and the attempt limit never engaged
([SPEC-121](SPEC-121-file-indexing-front-end.md), the outbox) — and during the first pass left the
index `Building` for the life of the process, where the retry of a failed first pass fires only on
`Failed`.

Two things about the failure are the rule:

- **It is a `TimeoutException`, never a cancellation.** Nobody cancelled, and telemetry that filed it
  as one would describe a shutdown where there was a hung server. The conversion is keyed on the
  caller's token, not on the exception type: a transport reports its own deadline as a cancellation
  too, and that is the backend's fault just the same. The caller's own cancellation passes through
  as what it is.
- **It is sized for a full embed window.** The deadline bounds one call, and a call is at most
  `EmbeddingBatchSizeChunks` chunks. Measured here at 386 ms per chunk on a CPU, a window of 64
  takes about 25 s; the default of 120 s is generous by a factor of five and still ends a hung call
  inside one delivery's lifetime. An operator who raises the window, or runs on a slower machine,
  raises the deadline with it.

The startup probe is bounded by the same deadline, so it cannot hang either. A probe that hits it is
retried like a refusal — a model paging in on its first call is exactly the transient the retries
exist for — and a probe that hits it three times fails the index, which is retried on its own
schedule.

**One deadline, and nothing retrying underneath it.** The client library carries two defaults of its
own that would sit under the rule above: a network timeout of 100 seconds whatever the host
configures, and a retry policy that tries a failed connection four times with backoff. Both are
overridden, and they go together. The retry policy is off, because the layers above this call already
retry — the probe three times, the outbox dispatcher with backoff to its attempt limit — and a third
underneath them multiplied the cost of a server that is not there: measured on 2026-09-24 on this
tree, a refused port cost one probe attempt four connections and 8 s, and the failure was reported as
"Retry failed after 4 tries" rather than as the refusal; a dropped connection was tried four times
too. With nothing retrying under the call, the SDK's own timeout would be the effective deadline for
any configured value above 100 seconds, so it is set to the configured one and one number bounds the
call. The chat client keeps the SDK's retries ([SPEC-140](SPEC-140-provider-adapters.md)): nothing
above a turn retries it.

### Composition and configuration

Two profiles, `ollama-blob` and `ollama-vec`, pairing the embedder with each vector backend. Both
report themselves **available everywhere the managed code runs**: whether the server is up and the
model pulled is a runtime condition, not a platform one, and `CompositionProfiles.Resolve` cannot
answer it without making a network call at startup.

Configuration is `Indexing.Ollama*`: endpoint (`http://localhost:11434/v1`), model, the
`model_version_id` vectors are stored under, the expected dimension, and the deadline on one call
(`OllamaTimeoutSeconds`, 120). The model-version-id is deliberately separate from the model name —
vectors are keyed by version, not by whatever the server happens to be serving today. A deadline of
zero or less is refused at construction: a call that may run forever is the defect the deadline
exists to close, not a setting.

### What this trades away

Every other profile is offline by construction: no configuration value can make the system reach the
network, because nothing in the assembly can. **This one can reach a socket.** It is localhost and
nothing leaves the machine, but the guarantee is now a property of the endpoint rather than of the
binary, and that is the cost of real semantics. `SPEC-000` states the rule this stays inside: an
implementation able to reach a *remote* service must not be a profile in this assembly.

## Non-functional requirements

- **Reliability** — a call that fails, or returns the wrong number of embeddings, or the wrong
  width, throws rather than degrading. Pairing results to inputs by position after a short reply
  would mislabel every vector in the batch.
- **Performance** — measured on the development machine, a single short document embeds in roughly
  200 ms against an already-loaded model. Not benchmarked at corpus scale; that measurement belongs
  with the indexing path that will drive it.
- **Operability** — a misconfigured or absent backend is reported once, at startup, by the health
  check, instead of only per call after indexing has begun.

## Implementation status

**Done, and verified against a real server on 2026-09-10:**

- `OllamaEmbeddingVectorizer`, the `ollama-blob`/`ollama-vec` profiles, and the `Indexing.Ollama*`
  configuration.
- Unit tests against a fake generator: instruction prefix, verbatim documents, width mismatch, count
  mismatch, empty batch. These need no server.
- `OllamaLiveIntegrationTests`, gated on a reachable server with the model. Confirmed on this
  machine: the model emits **1024** dimensions, matching the configured default, and a query
  retrieves the passage answering it while sharing none of its words — where the placeholder
  embedder, on the same corpus and query, does not.
- **A proactive startup health check** (`IEmbeddingHealthCheck`). The indexing service probes the
  backend once, before the first pass, when the composed vectorizer implements the seam. In-process
  embedders do not implement it and are not probed — they cannot be unreachable. A failed probe
  becomes a failed index carrying an actionable message, rather than a wall of per-file delivery
  errors that each describe a symptom. The probe retries three times, one second apart, so a server
  still paging the model in is not mistaken for an absent one; a width mismatch is passed straight
  through without retrying, because it is a configuration error that will fail identically each time.
  A probe that fails all three times fails the index — and that is retried on its own schedule
  rather than standing for the life of the process, since a server starting a minute late is the
  ordinary case ([SPEC-120](SPEC-120-rag-indexing.md), Readiness).
- **A bounded embed call** (2026-09-14). Every call carries the configured deadline and fails as a
  `TimeoutException` when it reaches it; the caller's own cancellation stays a cancellation. Asserted
  against a generator that never answers — the call ends within its deadline, as a timeout, with the
  model named; the caller's cancellation ends it as a cancellation; the probe retries it three times;
  a zero deadline is refused — and end to end through the dispatcher, where a hung embed retires its
  operation as failed within the deadline with `TimeoutException` recorded against it.
- **The client's own defaults overridden** (2026-09-24). Its network timeout is the configured
  deadline and its retry policy is off. The timeout is held on the options; the retries are observed
  through the real client against a loopback listener that drops every connection — the call fails
  after exactly one, where it had cost four — and through the host, where a refused Ollama fails the
  index in a few seconds at the default deadline, where each probe attempt had cost the whole of it.

Nothing is pending.

## What it retrieves, measured (2026-09-12)

The live verification above showed the embedder *works* — one query, one passage, sharing no words.
`SemanticSearchBenchmark` now says by how much, across a labelled corpus (`SPEC-131` carries the full
tables and the method).

- On twenty-two paraphrase queries over twenty documents, `qwen3-embedding:0.6b` puts the correct
  document first **every time** — Recall@1 of 100%, MRR@10 of 1.000. The placeholder embedder, on the
  identical corpus and queries, manages **none**.
- On three hundred generated documents with several relevant per query, it reaches **MAP 0.675** against
  the corpus-fitted embedder's 0.401 and the placeholder's 0.031.
- Both numbers reproduced exactly across three runs. Accuracy here is a property of the model and the
  corpus, not of the machine.

What it costs is the other half, and it is not small: **386 ms per chunk** on this machine's CPU, against
0.7 ms for the placeholder and 5.5 ms for the fitted embedder. A first index of a real folder is therefore
almost entirely this embedder, and nothing else in the pipeline is worth optimising until that is true no
longer — including the obvious one. Gathering chunks into one call instead of twenty does not reduce it
measurably here (`SPEC-120`), because what is being paid for is the model's inference and not the round
trip.

## Decision: a local embedding server becomes a prerequisite (2026-09-12)

The measurement above settles the relevance question, and settles it decisively, so the direction is
taken: **once this is the better embedder, installing it is a condition of running the assistant**
rather than an option an operator weighs. What follows is what that does and does not cost.

**What is given up is zero-install, not privacy.** The endpoint is localhost and nothing leaves the
machine, so the property every other profile has is untouched — a folder's contents still reach no
network. What stops being true is that a fresh clone runs on its own. That is a real cost and a much
narrower one than "offline" makes it sound, and the two should not be argued about as though they were
the same thing.

**The prerequisite is enforceable already**, which is the part that makes it defensible. The startup
probe above turns an absent or unreachable server into a failed index carrying an actionable message. A
prerequisite with no check is a trap: the system would otherwise start, index nothing useful, and answer
badly for a reason no operator could see.

**The other profiles do not go away, and a fallback is the least of why.** `programmable-*` stopped
being the default on 2026-09-15 and is the explicit semantics-free floor; `lsa-*` was the default for
one day and is the profile to name where nothing can be installed, because it is in-process and separates
answerable questions from unanswerable ones where the placeholder cannot (`SPEC-131`, embedder table). But
the load-bearing reason to keep the whole matrix is that **the matrix is the instrument**: every number
in this spec exists because a semantics-free embedder, a corpus-fitted one and a pretrained one could be
run over one corpus, and every number in `SPEC-131` exists because two vector backends could be run over
one set of vectors. A profile deleted for not being the default takes a measurement with it, and the
questions those measurements serve are open — what a corpus fit actually buys (`SPEC-161`), what a native
k-NN index actually buys (`SPEC-131`), and what any future embedder would have to beat. **Selecting a
non-default profile stays a supported thing to do, not a vestige.**

**`ollama-vec`, with `ollama-blob` where the native store has no binary.** The native k-NN backend
ships no `win-arm64` and no musl binary (`SPEC-131`), so it cannot be *required*; it is the default
where it loads, and the blob twin runs in its place where it does not — the same embedder, the same
embedding space, said at warning and on `GET /` (`SPEC-000`). That is the one fallback the system has,
and it is a store fallback, never an embedder one. The corpus size at which the native store starts to
pay is still the open question `SPEC-131` is holding; below it the twin costs little.

**What the default is now (2026-09-16).** `CompositionProfiles.Default` is `ollama-vec`, with
`ollama-blob` run in its place where the native store has no binary. The server is a prerequisite: an
Ollama that is not installed, not running, or without the model fails the index at the startup probe
with a message saying which, and searches refuse until it is fixed. No in-process embedder is ever
substituted for it, because the substitute would answer from a different embedding space with no way
for anyone to tell. The decision was the owner's, taken on 2026-09-16 over the `lsa-vec` default of the
day before; the figures it rests on were re-measured the same day on this tree, on Windows and on Linux
(next section). `lsa-vec` stays the profile to name where nothing can be installed.

## Measured again on this tree, on two operating systems (2026-09-16)

Ollama 0.34.1 installed on the development machine and, separately, run CPU-only in a container in a
WSL2 Podman machine on the same hardware with the tree copied in (`docs/benchmarks/linux/README.md`
says how). Every profile, both stores, both operating systems; the corpus and the queries are the ones
of 2026-09-12, unchanged since.

- **Accuracy is the same on both operating systems to the last digit**, and the same across the two
  stores on both: curated Recall@1 **95 %**, MRR@10 **0.977** for `ollama-vec` and `ollama-blob`
  alike; generated-corpus P@1 **82 %**, MAP **0.674** (Windows) / **0.675** (Linux, the rounding of one
  query). The placeholder and the fitted embedder reproduce their 2026-09-12 figures exactly.
- **One curated query fewer than on 2026-09-12**, when the model answered all twenty-two. The corpus
  has not changed, so the difference is in the model build or the server that serves it. Which query
  moved was not investigated; the finding is recorded, not explained.
- **What it costs, CPU-only:** about **600 ms per chunk** on Windows and **660 ms** in the Linux
  container on the same cores, against 1.8 and 3.5 ms for the in-process embedders. A twenty-document
  folder indexes from nothing in about twelve seconds. The 2026-09-12 figure of 386 ms was a different
  machine. The embed window still buys nothing measurable (1.03× at 64, spreads overlapping): inference,
  not the round trip, is what is paid for.
- **Query latency** with the pretrained model is about **175 ms** on Windows and **180 ms** on Linux,
  almost all of it embedding the query.
- **The backend gap reproduces on both** (`SPEC-131`): at 8,000 documents the native store answers in
  2.5 ms p50 against 35 ms for the blob store on Windows, 2.6 against 52 on Linux; at 500 documents both
  are under 4 ms.

The first two contaminated Windows timings — taken while the Linux container was embedding on the same
cores — were discarded and the run repeated with the container stopped; the accuracy columns did not
move between the two runs, which is the separation `SPEC-131` records.

## Open questions

- Whether the query instruction should ever be configurable. It is a constant today because getting
  it wrong degrades ranking silently, and an operator has no way to tell they have.
- ~~Whether `ollama-*` should become the default profile~~ — it did, on 2026-09-16 (above). What the
  cold-start cost now decides is how the README warns a first user about the first index of a large
  folder, not whether the default waits; the per-chunk figure above is still the thing to watch.

## References

- [System concept](SPEC-000-system-concept.md)
- [Embedding module](SPEC-160-embedding-module.md)
