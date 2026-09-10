# SPEC-162-embedding-ollama-local — Local Embedding via Ollama

| | |
|---|---|
| Status | Draft — core implemented and live-verified |
| Version | 0.2.0 |
| Owner | Embedding |
| Last updated | 2026-09-10 |

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

### Composition and configuration

Two profiles, `ollama-blob` and `ollama-vec`, pairing the embedder with each vector backend. Both
report themselves **available everywhere the managed code runs**: whether the server is up and the
model pulled is a runtime condition, not a platform one, and `CompositionProfiles.Resolve` cannot
answer it without making a network call at startup.

Configuration is `Indexing.Ollama*`: endpoint (`http://localhost:11434/v1`), model, the
`model_version_id` vectors are stored under, and the expected dimension. The model-version-id is
deliberately separate from the model name — vectors are keyed by version, not by whatever the server
happens to be serving today.

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
- **Operability** — failure is currently only visible per call. There is no startup probe, so an
  operator who has not started Ollama learns about it when indexing fails rather than at boot. See
  Pending.

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

**Pending:**

- **A proactive startup health check.** Nothing probes endpoint-and-model before indexing begins, so
  the failure surfaces later and less clearly than it could.
- **A bounded embed call.** The per-request timeout is not yet applied, so a call that never returns
  is not yet cut off. The configuration key for it is deliberately absent until the code that reads
  it exists.

## Open questions

- Whether the query instruction should ever be configurable. It is a constant today because getting
  it wrong degrades ranking silently, and an operator has no way to tell they have.
- Whether `ollama-*` should become the default profile. That is a measurement, not a preference, and
  the benchmark that would settle it has not been run against this embedder.

## References

- [System concept](SPEC-000-system-concept.md)
- [Embedding module](SPEC-160-embedding-module.md)
