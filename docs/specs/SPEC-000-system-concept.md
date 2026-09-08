# SPEC-000 — System Concept

| | |
|---|---|
| Status | Draft — placeholder |
| Version | 0.1.0 |
| Owner | — |
| Last updated | 2026-09-08 |

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
- **Interchangeable by configuration.** Swapping an implementation should be a composition
  change, not an edit spread across callers.
- **Versioned explicitly.** Stored data outlives the code that wrote it. Schema and model
  identity are recorded, never inferred.
- **Offline by construction where it matters.** The default path should not require a
  network service to be reachable.
- **Observable enough to debug.** A loop that survives its own faults and keeps going looks
  identical to one that is working, unless it says otherwise.

## Architecture at a glance

- **Ingest** — walk the analyzed folder, filter, chunk.
- **Embed** — turn chunks into vectors through a replaceable implementation.
- **Store** — one folder-scoped database holding the manifest, the chunks and the vectors.
- **Retrieve** — embed a query, rank against stored vectors, assemble what fits a budget.
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
