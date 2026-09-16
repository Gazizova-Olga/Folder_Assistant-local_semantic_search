# SPEC-000 — System Concept

| | |
|---|---|
| Status | Draft — placeholder |
| Version | 0.5.0 |
| Owner | — |
| Last updated | 2026-09-16 |

## Purpose

Folder Assistant is a local agent that indexes one folder and answers questions about
its contents. This document holds the parts that are true of the whole system, so the
module specifications below it do not each restate them.

## Scope

**In scope**

- The system boundary: what runs in this process, and what it talks to.
- Principles every module is held to.
- The specification set itself, and how it is kept honest.

**Out of scope**

- Module behaviour. Each `SPEC-1xx` owns its own.

## The specification set

| Spec | Subject |
|---|---|
| [SPEC-100](SPEC-100-conversation-orchestration.md) | Conversation orchestration |
| [SPEC-101](SPEC-101-file-tools.md) | File tools and their containment |
| [SPEC-110](SPEC-110-rag-retrieval.md) | Retrieval |
| [SPEC-120](SPEC-120-rag-indexing.md) | Indexing |
| [SPEC-130](SPEC-130-persistence.md) | Persistence |
| [SPEC-140](SPEC-140-provider-adapters.md) | Provider adapters |
| [SPEC-150](SPEC-150-shared-abstractions.md) | Shared abstractions |
| [SPEC-160](SPEC-160-embedding-module.md) | Embedding: common requirements |
| [SPEC-161](SPEC-161-embedding-programmable.md) | Embedding: in-process implementations |
| [SPEC-162](SPEC-162-embedding-ollama-local.md) | Embedding: local Ollama |
| [SPEC-163](SPEC-163-embedding-online.md) | Embedding: hosted API |
| [SPEC-900](SPEC-900-versioning-and-migration.md) | Versioning and migration |
| [SPEC-910](SPEC-910-observability-and-slos.md) | Observability and service levels |
| [SPEC-920](SPEC-920-security-and-compliance.md) | Security and compliance |
| [SPEC-930](SPEC-930-release-gates.md) | Release gates |

**Most of these are placeholders right now**, and say so at the top. They exist as a set
from the start so that a module being built has somewhere to record its decisions at the
time they are taken, rather than somewhere to write them up afterwards. A placeholder is
an admission of what is not yet decided; it is not a description of anything.

## Principles

- **Contract first.** A replaceable part is replaceable because an interface says what it
  must do, not because two implementations happen to look alike.
- **Interchangeable by named profile, inside the trust boundary.** Which implementations run
  — vectorizer, vector store, retrieval strategy — is decided by one configuration value
  naming a profile: an *enumerated bundle* defined in code, never a set of independent
  per-module switches. Per-module flags would describe a combinatorial space in which most
  points are meaningless and some are dangerous — a reader looking somewhere other than
  where the writer wrote does not fail, it reports that nothing has ever been embedded and
  re-embeds the folder on every run. A bundle makes those combinations unnameable, and gives
  every runnable configuration a name that can be put in a benchmark result.
- **A profile may not cross the trust boundary.** A profile name in configuration *is* a
  runtime flag, and that is only safe because every profile compiled into this binary is
  offline and local: no configuration value can make the system reach the network, because
  nothing in the assembly can. An implementation that changes that must not be a profile
  here — it belongs in a separate assembly or publish, so the guarantee stays "it is not in
  the binary" rather than "the configuration says not to". That, and not modularity, is the
  criterion for ever splitting an assembly; the contracts already provide the modularity.
  **The chat provider is the one deliberate exception** ([SPEC-140](SPEC-140-provider-adapters.md)):
  it is not a profile, it is chosen by the operator in `Provider` configuration and by nothing else,
  and it is the one place document text can leave the machine. The sentence above is true of
  embedding and retrieval and of nothing that talks to a chat model.
- **A named profile that cannot resolve is a startup failure, never a fallback.** An unknown
  name, or one whose implementation is unavailable on this platform, throws. Falling back
  would run a configuration nobody asked for while reporting success, and the result would be
  a *working* system answering out of a different embedding space than the operator believes
  — the silently-plausible wrong answer this system is built against.

  **The one fallback there is applies to the default alone, and is never silent** (2026-09-15).
  When no profile is configured, the default is `ollama-vec` (2026-09-16; it was `lsa-vec` for the
  day before); where the native store has no binary, `ollama-blob` runs in its place — the same
  embedder, so the same embedding space, over the blob store — and the composition root logs the
  substitution at warning and `GET /` reports the active profile with a note saying which was
  wanted. Nothing an operator named is ever swapped, and nothing swapped is ever unreported: those
  two properties are what the rule above protects, and both hold.

  **A fallback never changes the embedder.** The default's embedder needs a local Ollama server
  holding the model. When that server is not there, the startup probe fails the index with a
  message saying so, searches refuse until it is, and nothing runs in its place — an in-process
  embedder substituted for a pretrained one would answer every question from a different embedding
  space, plausibly and worse, which is the one outcome this document exists to forbid.
- **Versioned explicitly.** Stored data outlives the code that wrote it. Schema and model
  identity are recorded, never inferred.
- **Local by construction where it matters.** No document text leaves the machine on the
  default path. The default embeds through a server on loopback (2026-09-16), so it needs a
  local process to be running; it needs nothing beyond the machine to be reachable. Profiles
  that need no process at all exist and are named explicitly.
- **Observable enough to debug.** A loop that survives its own faults and keeps going looks
  identical to one that is working, unless it says otherwise.

## Architecture at a glance

- **Ingest** — walk the analyzed folder, filter, chunk.
- **Embed** — turn chunks into vectors through a replaceable implementation.
- **Store** — one folder-scoped database holding the manifest, the chunks and the vectors.
- **Retrieve** — embed a query, rank against stored vectors, assemble what fits a budget.
  Composed as of this version: the active profile registers `IRetrievalQuery` in the
  composition root, so it is resolvable from the container. **Nothing on the request path
  calls it yet** — the agent still answers without consulting the index, and until that lands
  the indexing and retrieval machinery, including its measured performance, serves nothing at
  runtime.
- **Converse** — route a turn to an agent that can call the tools above.

## Keeping the specs honest

A specification that disagrees with the code is worse than no specification, because it is
believed. Two rules follow:

1. A change to behaviour, data or an interface updates its spec **in the same commit**.
2. Where the code deliberately departs from a spec, the departure is recorded in the spec
   rather than left for a reader to discover.

## Open questions

- Whether the placeholder specs that stay empty long enough should be deleted rather than
  left standing as a promise.

## References

- [Library architecture](../diagrams/library-architecture.md)
