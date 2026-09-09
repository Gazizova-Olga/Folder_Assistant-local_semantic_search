# SPEC-150-shared-abstractions — Shared Abstractions

| | |
|---|---|
| Status | Draft — partly written |
| Version | 0.2.0 |
| Owner | — |
| Last updated | 2026-09-09 |

## Purpose

Holds the contracts, records and version metadata that more than one module depends on.

## Scope

**In scope**

- Cross-module contracts and DTOs.
- Model and schema version identity.

**Out of scope**

- Any single module's internal types.

## Requirements

**The contracts live in the application assembly. There is no separate abstractions library,
and that is a decision rather than a deferral.**

The contracts that cross module boundaries — `IVectorizer`, `IVectorStoreReader` and
`IVectorStoreWriter`, `IRetrievalQuery`, `IFolderManifestReader`, `IIndexState`,
`IFileChangeFeed` — sit in the folders of the modules that define them, and every consumer is
in the same assembly.

**Why not a `Shared.Abstractions` project.** A separate assembly buys one thing: it makes a
dependency physically impossible to take rather than merely wrong. That is worth paying for
when independent teams or independent release cadences would otherwise take it. Here there is
one deployable, one release, and one author, so what it would actually buy is a project
reference to maintain and a compile step for every consumer of a contract — while the rule it
enforces ("depend on the interface") is already enforced by the interfaces existing.

**What would change the decision.** A second deployable that needs the same contracts — a
separate indexing worker, most plausibly. At that point the split is mechanical, because the
contracts are already free of implementation detail: they name records and interfaces, and none
of them reaches for SQLite, the filesystem or a provider SDK. That property is the thing worth
protecting, and it is what makes this a cheap decision to reverse.

**What must not happen in the meantime**: a contract growing a dependency on an implementation
type. That is what would make the future split expensive, and it would not be caught by a
compiler while everything shares an assembly.

## Contracts

## Non-functional requirements

- Reliability:
- Performance:
- Operability:

## Open questions

## References

- [System concept](SPEC-000-system-concept.md)
