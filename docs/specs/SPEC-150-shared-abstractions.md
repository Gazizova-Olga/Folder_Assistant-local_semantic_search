# SPEC-150-shared-abstractions — Shared Abstractions

| | |
|---|---|
| Status | Draft — partly written |
| Version | 0.4.0 |
| Owner | — |
| Last updated | 2026-09-12 |

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
`IVectorStoreWriter`, `IRetrievalQuery`, `IFolderManifestReader`, `IIndexState` — sit in the
folders of the modules that define them, and every consumer is in the same assembly. The one
contract that crosses an assembly boundary, the indexing front end's vectorization seam, is defined
by the library that calls it and implemented here ([SPEC-121](SPEC-121-file-indexing-front-end.md)).

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

**It was broken within a day of being written down.** `SqliteVecVectorStoreReader` held a
concrete `SqliteBlobVectorStoreReader`, to borrow the manifest-facing reads — chunk locations
and the fit artifact — which genuinely are backend-independent. The substance was sound; the
shape was exactly what this rule forbids, and with no compiler enforcing it, nothing objected.

**The fix, and the general rule it illustrates**: shared behaviour goes into a type that *both*
implementations use — `Persistence/ManifestReads.cs` — never into one of them for the other to
reach into. Where two implementations legitimately share code, that is evidence of a third
thing, not of a dependency between them.

## Test Strategy

**Shared contract suite: `VectorStoreContractTests`.** One set of tests, run identically against
every implementation of `IVectorStoreWriter` / `IVectorStoreReader` / `IRetrievalQuery`,
parameterised over composition profiles. A new backend is added to its `Backends` list and
inherits the whole suite. Until a second implementation existed there was nothing to
cross-check; the arrival of the `sqlite-vec` backend is what made it worth building.

**It was built because per-backend tests had already let a real defect through.** The question
"has this file already been embedded?" was answered against `chunk_vector`. The blob backend's
own tests passed, because the blob backend writes `chunk_vector`. Under `sqlite-vec`, which does
not, every file would have been re-embedded on every run — silently, with the index still
looking correct. Verified by mutation: reintroducing that query fails exactly one
parameterisation of `Reindexing_An_Unchanged_Folder_Embeds_Nothing` and leaves the other green.

The lesson generalises past this defect. **A per-implementation test can only assert what that
implementation happens to do.** Only a test written once and run against all of them asserts
what the *contract* requires, which is the property this whole single-assembly design rests on.

## Contracts

## Non-functional requirements

- Reliability:
- Performance:
- Operability:

## Open questions

## References

- [System concept](SPEC-000-system-concept.md)
