# SPEC-121 — File-Indexing Front End

| | |
|---|---|
| Status | Draft |
| Version | 0.4.0 |
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
- Processing each settled change against the index, one file at a time.
- A durable outbox of per-file operations with at-least-once delivery and retry.

**Out of scope**

- Chunking, embedding, the vector store, the manifest schema ([SPEC-120](SPEC-120-rag-indexing.md),
  [SPEC-130](SPEC-130-persistence.md), [SPEC-160](SPEC-160-embedding-module.md)).
- Retrieval and ranking ([SPEC-110](SPEC-110-rag-retrieval.md)). Nothing here reads the index.

## Implementation status

**The watcher, the reconciler and the per-change pipeline are built. The outbox is not.** There is no
durable operation log, no dispatcher and no bridge to the indexing subsystem yet. Nothing yet composes the
watcher and the pipeline together, and both writers apply their conclusions to a store the
application has not yet implemented. The sections below describe only what exists; the rest of the design is named in Scope
so the gap is visible, and will be specified as it is built rather than promised here.

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

## Reconciliation, and why its own failures matter more than most

Watching is prompt and imperfect. The reconciler compares the whole folder against what the index
recorded and repairs the difference, which is what makes a dropped event temporary rather than
permanent.

**Because it is the safety net, a reconciler that stops running looks exactly like one with nothing
to do.** There is no error, no queue backing up, and no symptom until much later, when a search
quietly fails to find a file that is plainly there. Three rules follow, and each is verified by
breaking it rather than asserted in a comment.

**A pass must survive a file it cannot read.** The indexer does not own the folder it indexes: an
editor holding a file open, a build writing one, a checkout replacing one — all deny the
share-`Read` open this needs, and all are ordinary rather than exceptional. Hashing runs in
parallel, and a parallel loop cancels its remaining work when one body throws, so a single locked
file would otherwise end the pass for every file after it.

**A file that could not be hashed is left out of the pass's picture entirely** — never recorded with
an empty or placeholder hash. A blank becomes that file's stored identity, and since every
unhashable file would carry the same blank, any comparison keyed on content sees a folder full of
identical files.

**A skipped file is not a deleted one.** It is on disk and merely unreadable this pass. Reading its
absence from the pass as a deletion would drop its index entry and re-add it on the next pass — an
endless delete-and-restore cycle driven entirely by someone else holding the file open.

**And a failed pass must not end the loop.** A fault here costs one interval of staleness, because
the next pass re-reads the whole folder from scratch. Letting it escape costs every future pass.

The walk does not follow reparse points. One can point above the root or back into the tree,
turning a bounded walk unbounded and putting files from outside the watched folder into its index.

Hashing parallelism is sized independently of anything else. It is a disk- and CPU-bound job that
scales with the machine, unlike delivery onward, which is one round trip per file into a single
backend. A single knob for both could only ever suit one of them.

**Change detection does not use a cryptographic hash.** The question is whether these bytes differ
from the last ones seen, nothing downstream treats the answer as an identity or a signature, and it
is computed for every file on every pass. It is deliberately a different hash from the one the
indexing subsystem uses to address chunk content, which must be stable across machines because it
keys stored rows.

## The per-change path

A settled change names a file. It does not say what happened to that file, and the pipeline does not
ask: by the time a change is processed the file may have been recreated, deleted again, or saved with
identical bytes. **The event's kind is not read.** What is on disk now, against what the index
recorded, decides whether the file was added, modified or removed — the same comparison the
reconciler makes, for one file. Identical content costs no write.

**Both writers key a file identically**, through one rule. Keyed differently, each would read the
other's record as a different file: the second writer would add it again, and the first record would
never be removed.

**A file is settled before it is hashed.** A change has already gone quiet for a whole debounce
window by now, but a quiet window measures events, not writers. Two kinds of writer survive it: one
that holds its handle, which denies the share-`Read` open, and one that shares the file while writing
it, so the open succeeds and the content keeps moving. The probe checks for both — an open, and a size
and write time that hold still across an interval.

**A busy file delays a change; it never drops it.** A change that meets a writer — at the probe, or at
the hash, since the two are separate opens and a writer can take the file in between — is tried again
after a delay. Treating it as a failed change would drop it with nothing behind it to try again, and
the file would stay stale until a reconcile happened to pass over it.

**Retries are bounded.** After a fixed number of attempts the file is left to the periodic reconcile,
which retries every file it could not read on every pass anyway. An editor can hold a file for hours,
and chasing it on the event path as well would only hold a retry open for as long as the writer holds
its handle.

**A change waiting out a retry is not discarded when its source completes.** A change is outstanding
from the moment it is taken until it finishes, including while it waits; processing ends only once the
source has completed *and* nothing is outstanding. Ending when the source ends would discard exactly
the changes that met a busy file.

**A fault on one change does not stop the ones behind it.** Anything other than a busy file — the store
refusing a write, say — drops that one change, and the periodic reconcile restates the file from disk.

The pipeline reads the index **one record at a time**. A single event concerns a single file, and
reading every record to answer it would be a cost that grows with the corpus on every save.

A folder created, moved in or deleted arrives as one event for the folder, not one per file inside it.
There is no file at that path to hash, so the change is a removal if the index held a file there and
nothing otherwise; the files beneath the folder are found by the next reconcile.

## A file's timestamps are the file's

A record carries the file's **creation time as the filesystem reports it** — never the moment a writer
ran. A creation date that really means "when this folder was first indexed" is not metadata about the
file, and every file in a folder indexed at once would share it.

- **Where no creation time is reported**, the file API gives the 1601 file-time epoch. The write time
  stands in: it is the closest true statement available about the file's age, where the epoch would
  date every such file to the same impossible moment.
- **A modified file keeps the creation time already recorded for it.** Editing a file does not create
  it.
- **Both writers use one rule.** If the reconciler and the per-change path derived it differently, a
  file's recorded age would depend on which of them discovered it.

Nothing reads the value yet. It is recorded state, like a record's size, and it is recorded correctly
from the first writer that sees a file rather than corrected after the fact.

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

The settle probe's wait between its two looks is replaceable, so a write or a delete landing between
them is staged exactly rather than raced against a real delay. A writer taking a file between the
probe and the hash cannot be staged on demand at all, so the pipeline's retry is tested with a hasher
that loses a set number of times. The pipeline's tests require a run to **finish on its own** rather
than cancelling it after a deadline: a run that never finished would otherwise stop quietly and leave
its assertions to pass or fail on whatever it managed first.

The creation-time rule is asserted as a function, including the no-creation-time fallback, which no
filesystem can be made to produce on demand. Each writer is then tested against a backdated file, so
its own clock cannot coincide with the value it should record. Setting a creation time is not supported
everywhere, so those tests compare exactly against what the filesystem reports — which a clock reading
cannot match — and additionally against the backdated value wherever the backdating took, since the
first comparison uses the rule itself as its expected value and cannot see the rule being wrong.

## Open questions

- Whether the settle loop should poll or schedule per path. Polling is used, because a burst
  touching a thousand files would otherwise schedule a thousand timers to do one pass's work, and
  the poll interval is already bounded by the window it detects. Worth revisiting if a very long
  window makes the latency noticeable.
- Whether a folder moved in or deleted should be expanded into per-file changes on the event path. Today
  its files wait for the next reconcile, which bounds the delay by the reconcile interval rather than by
  the debounce window.
- Whether an in-process writer should report changes directly. The application's own file tools will
  write into this folder, and rediscovering their writes through the operating system is wasteful —
  but a direct path must feed the settling rule rather than bypass it, or a batch of edits stops
  being coalesced at all.

## Related specs

- [SPEC-120 — RAG Indexing](SPEC-120-rag-indexing.md)
- [SPEC-130 — Persistence](SPEC-130-persistence.md)
