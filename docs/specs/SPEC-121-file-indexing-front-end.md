# SPEC-121 — File-Indexing Front End

| | |
|---|---|
| Status | Draft |
| Version | 0.1.0 |
| Owner | Indexing |
| Last updated | 2026-09-11 |

## Purpose

Detect changes in the analyzed folder and deliver each changed file to the indexing subsystem
durably — surviving a restart, and surviving the folder being written to while it is read.

## Why this is a separate module

[SPEC-120](SPEC-120-rag-indexing.md) describes a **corpus-oriented** indexer: scan the whole folder,
diff by content hash, write in one transaction. That is the right shape for a cold start and the
wrong shape for an editor saving a file. A per-file, event-driven, durable path is a different
design with different failure modes, and putting it inside the corpus pipeline would give one type
two jobs whose correctness arguments do not overlap.

It is a **separate assembly**, not just a namespace. The boundary is one-way by construction: this
library knows nothing about chunking, embedding, vectors or retrieval, and cannot be made to
depend on them by accident. What crosses back is a seam the application implements.

## Scope

**In scope**

- Watching the folder: debounced, coalesced, and deliberately coarse.
- Reconciling periodically, as the safety net for events the watcher never delivers.
- A durable outbox of per-file operations with at-least-once delivery and retry.

**Out of scope**

- Chunking, embedding, the vector store, the manifest schema ([SPEC-120](SPEC-120-rag-indexing.md),
  [SPEC-130](SPEC-130-persistence.md), [SPEC-160](SPEC-160-embedding-module.md)).
- Retrieval and ranking ([SPEC-110](SPEC-110-rag-retrieval.md)). Nothing here reads the index.

## Implementation status

**The watcher front end is built. Nothing else in this spec is.** There is no reconciler, no
outbox, no dispatcher and no bridge to the indexing subsystem yet, and nothing consumes the changes
this stage publishes. The sections below describe only what exists; the rest of the design is named
in Scope so the gap is visible, and will be specified as it is built rather than promised here.

## The signal is deliberately coarse

A published change carries a path and one of three kinds — created, modified, deleted. It carries
no revision, no byte range, no timestamp and no content.

That is not an omission to be filled in later. The consumer re-diffs by content hash before doing
any work, so richer detail would be **unused and load-bearing at the same time**: unused because
nothing reads it, load-bearing because it would have to be correct. And it cannot be made correct,
because filesystem watching is unreliable in two specific ways that a detailed signal would have to
paper over:

- **Events are dropped when the operating system's buffer overflows.** A burst is exactly when the
  index most needs to keep up, and exactly when events go missing. A reconciler is what heals this,
  not a better event.
- **A save is often not a write.** Editors write a temporary file and rename it over the target, so
  the "modification" arrives as a create and a delete of two different paths.

A coarse signal is robust to both, because it claims almost nothing.

## Settling

A single save is rarely a single event, and a build touches hundreds of files at once. Every raw
event restarts a quiet window for its path; a path is published once it has been untouched for the
whole window.

**Events for one path fold into their net effect** rather than queueing:

| pending | then observed | published |
|---|---|---|
| created | modified | created |
| created | deleted | **nothing** |
| deleted | created | modified |
| modified | deleted | deleted |
| modified | modified | modified |

Two of those rows are decisions rather than bookkeeping.

**Created-then-deleted annihilates.** A file written and cleaned up inside one window never existed
as far as anything downstream is concerned, and reporting the pair costs an index and an un-index
of a document that is already gone. This is the ordinary shape of a scratch file — and of the
temporary file an atomic save leaves beside its target.

**Deleted-then-created is a modification, not a create.** That sequence *is* save-via-rename. The
consumer may already know the path, so reporting a create risks it being refused as a duplicate;
reporting a modification is true either way.

**The settling rule holds no clock.** The caller supplies the current time on every call, so the
behaviour is tested by advancing a variable rather than by sleeping — which is the difference
between asserting the rule and asserting that the machine was fast enough.

## What is never reported

- **The metadata folder.** This is not an optimisation and not a tidiness rule. The index lives
  inside the folder it indexes, so every write it makes lands under the watched tree; unfiltered,
  indexing causes an event which causes indexing, with no idle state to settle into. The
  **configured** folder name is honoured, not the default one — a deployment that renamed it would
  otherwise have the index feeding itself.
- **Build output and tooling directories** — `bin`, `obj`, `.git`, `.vs`, `node_modules` — matching
  the set the corpus scanner already skips, so the two walkers cannot disagree about what the
  corpus is.
- **`*.tmp`** — the transient file an atomic write leaves beside its target, holding a half-written
  copy of a document that is about to be reported in its own right.

Exclusion matches a **whole path segment**, never a prefix. A folder called `binaries` or `objects`
is an ordinary folder.

## Non-functional requirements

- **A lost event must not be fatal.** The overflow notification tears down nothing; a reconciler is
  the mechanism that heals it, and a watcher that stopped on overflow would turn a burst of
  activity into a permanently blind index.
- **Shutdown does not discard held edits.** Whatever is still inside its quiet window is published
  rather than dropped. The edits are real and already made; reporting them a moment early costs a
  hash of an unchanged file, while dropping them leaves the index stale until something else
  happens to notice.
- **A change may be published for a file whose content did not change.** Touching a file settles and
  is reported. The consumer discovers there is nothing to do — which is cheaper than this stage
  trying to be sure.

## Test strategy

The settling rule and the exclusion rule are asserted **as functions**, against a supplied clock and
a supplied path. Neither needs a filesystem, and testing them through a real watcher would assert
the operating system's timing alongside the rule.

One class does drive a real `FileSystemWatcher` against a real folder, because nothing else can show
that operating-system events reach the settling rule at all — the two halves could be correct and
wired together wrongly. Its deadlines are deliberately generous: a slow machine should make it slow,
not red.

## Open questions

- Whether the settle loop should poll or schedule per path. Polling is used, because a burst
  touching a thousand files would otherwise schedule a thousand timers to do one pass's work, and
  the poll interval is already bounded by the window it detects. Worth revisiting if a very long
  window makes the latency noticeable.
- Whether an in-process writer should report changes directly. The application's own file tools will
  write into this folder, and rediscovering their writes through the operating system is wasteful —
  but a direct path must feed the settling rule rather than bypass it, or a batch of edits stops
  being coalesced at all.

## Related specs

- [SPEC-120 — RAG Indexing](SPEC-120-rag-indexing.md)
- [SPEC-130 — Persistence](SPEC-130-persistence.md)
