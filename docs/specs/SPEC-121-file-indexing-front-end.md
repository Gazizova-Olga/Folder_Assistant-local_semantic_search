# SPEC-121 — File-Indexing Front End

| | |
|---|---|
| Status | Draft |
| Version | 0.13.0 |
| Owner | Indexing |
| Last updated | 2026-09-12 |

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
- Accepting a change reported by a writer inside this process, onto that same settling path.
- A durable outbox of per-file operations with at-least-once delivery and retry.

**Out of scope**

- Chunking, embedding, the vector store, the manifest schema ([SPEC-120](SPEC-120-rag-indexing.md),
  [SPEC-130](SPEC-130-persistence.md), [SPEC-160](SPEC-160-embedding-module.md)).
- Retrieval and ranking ([SPEC-110](SPEC-110-rag-retrieval.md)). Nothing here reads the index.

## Implementation status

**Every seam this library defines now has an implementation — the watcher and its write-through
entry point, the reconciler, the per-change pipeline, the outbox dispatcher, the vectorization seam,
and the store behind the writers and the dispatcher. Nothing composes them.** What is missing is the
wiring that starts the watcher, runs the passes and drains the queue; until it exists the subsystem
is complete and driven by nothing. The sections below describe only what exists; the rest of the
design is named in Scope so the gap is visible, and will be specified as it is built rather than
promised here.

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

## Reporting a change from inside this process

The front end normally *discovers* changes. That is the only option for an edit made by anything
outside this process — an editor, a checkout, a build — and it is the wrong one for an edit this
process makes itself, where the writer knows the path the moment its handle closes. Waiting to be
told about its own write buys a discovery round-trip and nothing else: the file is already settled.

`IIndexChangeNotifier` is that entry point, and its surface is three reports — a file created, a
file written in place, and a file deleted. The first two are kept apart because the folding rules
keep them apart: a create and a delete inside one window annihilate, so a file written and cleaned
up inside one costs nothing, while the same pair reported as modifications folds to a deletion of a
path that was never indexed. That ends in the same place only because the consumer looks for a
record to remove and finds none — luck rather than design, and it runs out the moment either rule
changes. What matters about it is **where a report feeds**, not that it exists: a
reported change is recorded in the same debouncer the watcher's own events are recorded in, and from
there is indistinguishable from one.

- **Several writes to one file cost one pass.** A writer applying a dozen edits, or a burst of
  separate calls, settles as one change — exactly as an editor saving repeatedly does. Reporting
  straight to the per-change path instead would give that case a settle-hash-embed cycle per write,
  which is *worse* than not reporting at all and letting the watcher find it.
- **A report merges with the watcher's own events for that file.** The write raises those events
  too. Both land on one pending entry; two would mean reporting bought a duplicate pass rather than
  an earlier one.
- **The exclusions apply unchanged.** A report is not a way around them, least of all for the
  metadata folder — a writer reporting the index's own bookkeeping would feed exactly the loop the
  watcher exists to refuse.
- **A path is a key, so it is canonicalised.** The watcher reports a full path and a caller reports
  whatever it built; two spellings of one file would be two pending entries, and the merging above
  would fail with nothing to see.

Reporting is **advisory**: it changes when a change is indexed, never whether. A report made while
nothing is running is dropped rather than held — nothing would drain it, and the reconcile that
follows a start compares the whole folder regardless. A caller must not fail its own operation
because a report was refused.

**Writes further apart than the quiet window are separate changes**, which is correct for an editor
and wrong for a caller that pauses between its own writes. That case is what a hold answers.

### Holding a batch

The quiet window merges writes that are close together in time. It cannot merge writes separated by
the caller stopping to think between them, and that is the ordinary shape of a multi-step edit: each
write lands in a window of its own and costs its own pass. Raising the window is the wrong answer —
it delays every ordinary external edit by the same amount to fix a case it cannot identify.

`BeginBatch` puts the boundary where the knowledge is. It holds publishing until the returned handle
is disposed; what accumulates in the meantime keeps coalescing, so a file written five times leaves
one entry and is published once.

- **A hold covers everything pending, not only what was reported through the seam.** A caller's own
  writes reach the debouncer through the watcher as well, so a hold that suppressed one source and
  published the other would suppress nothing that matters. An unrelated editor saving during a held
  window waits it out, which is the price of not indexing one file once per step of one edit.
- **Holds nest and are counted**, because the boundaries they mark nest: one around a whole piece of
  work, one around a step inside it. Releasing the inner must not publish what the outer still
  holds; the last release is what lets the batch go.
- **A hold expires by itself** after `MaxHoldDuration` (two minutes), whether or not it is ever
  released. This is the same rule as a reconcile pass surviving its own failure: a caller that
  crashes or leaks a handle must cost a bounded delay, never an index that stops converging for the
  life of the process. The expiry runs from the **first** hold — if nesting extended it, a caller
  opening one per step would have exactly the unbounded hold it exists to rule out.
- **Releasing the last hold publishes immediately** rather than waiting out the remaining poll
  interval, since the caller has just said its work is finished.
- **Disposal is idempotent.** Disposing one handle twice would otherwise release a hold belonging to
  someone else, and let that caller's half-finished work out.
- **A scheduled reconcile waits too.** The reconciler is the one path that reaches the index without
  going through the debouncer, so a hold cannot reach it the way it reaches everything else: a pass
  landing inside one reads and records a file its holder is still part-way through editing. It
  waits, bounded by the hold's own expiry — delaying a safety net by at most that costs nothing
  worth having, since it exists to catch what the watcher missed and not to meet a deadline.

What a hold promises is narrow and worth stating exactly. **No file is published more than once for
the work one hold covers**, and a file created and removed inside it is never published at all. It
does not promise one change for the whole batch: four files edited under one hold are four files,
and cost four passes.

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

It reaches the embedding side as part of each delivered file's metadata.

## Delivery: the outbox

Recording a change and delivering it are two steps, and only the first is quick. The store **queues a
delivery in the same write that records the change** — an upsert for an added or modified file, a delete
for a removed one — so a recorded change can never be forgotten: nothing would come back for it, because
the next comparison would find the record already matching the disk. A dispatcher then drains the queue
into the embedding side, one seam the application implements.

**Delivery is at-least-once.** An operation is claimed, delivered, and only then retired. A process that
stops mid-delivery records no outcome; its claimed operations return to the queue when the next one starts.
That is affordable only because repeating a delivery costs nothing: **content already delivered is not
delivered again**, judged by the content hash last confirmed for the file.

**The delivered id is `sha256("file::" + relative key)`**, exactly the id the application's corpus scanner
derives, so a file delivered on its own lands on the same rows a whole-folder scan wrote. The two
derivations live in two assemblies by agreement, and a test runs the scanner to hold them to it.

**One file's operations run in the order they were queued, never concurrently.** A claimed batch is split
by file: parallel across files, sequential within one. An upsert and a delete for the same file, run
together or out of order, can leave content embedded for a file that no longer exists. How many files run
at once defaults to one, because each delivery is a round trip into a single backend and what that backend
can take in parallel is a property of the backend.

**Recording a file as delivered is conditional on the content that was delivered.** A delivery is a round
trip, and the file can be rewritten while it happens. The dispatcher records the hash it read before
delivering, and the store accepts it only if the file's recorded content is still that hash. Writing the
record back whole instead would restore the old hash over the new one; the delivery already queued for the
new content would then find it "already delivered" and skip, leaving the file on stale content with nothing
left to correct it. **Only the dispatcher states what was delivered** — the writers' record has no such
field — so no writer can revert it either. Losing a mark is safe, because the queued delivery repeats the
work. Losing an edit is not.

**A failed delivery waits, then tries again**, doubling the wait each time up to a cap, so a backend that is
down is not hammered and a backend that recovers is found.

**A delivery that will not be tried again is retired as failed, keeping the error that ended it.** Retired
as done, an outbox full of abandoned work would look exactly like one where everything had arrived, and the
file's only symptom would be a search that quietly does not find it. It is the **last** error that is kept:
a run of "connection refused" ended by "model not found" is only actionable if the second survives.

**A failed drain does not end the dispatcher's loop**, for the reconciler's reason — every queued file
depends on it. A drain that failed part-way can leave operations claimed and never resolved, so the next
pass returns them to the queue before claiming again.

**When a burst of deliveries ends, the dispatcher invites the store to checkpoint** — once per burst, at
the moment a drain that delivered work finds the outbox empty. Never per operation, which would block
readers again and again to reclaim the same space; never on an idle poll, which would run forever against
a folder nobody is touching; and not after a drain that failed, which learned nothing about whether the
burst is over. The invitation is advisory: a store with no such concept ignores it, and one that cannot
take it right now may fail without consequence — a checkpoint reclaims disk and delivers nothing, so it
must not end the loop. What a checkpoint is for, and what it was measured to reclaim, is in
[SPEC-130](SPEC-130-persistence.md).

## Writing a file's record: own your columns

Nothing in this library writes back a record it read. A pass states conclusions — added, modified,
removed, and what the scan saw — and every delivery write either names one operation by its id or
states a condition (`TryMarkSyncedAsync`, above). That is a requirement on whatever implements these
seams, not a description of taste, and it binds an implementation harder than it binds this library,
because the implementation is where the columns actually are.

**The rule: write the columns you own, not the row you read.** A writer that reads a record, does
something slow, and writes the whole thing back reverts whatever another writer recorded in between —
silently, since the row it writes is entirely plausible. The **delivery mark** is the column where
this costs something, because it is what decides whether a queued operation still has work to do.
Two ways to lose it, both reachable through the conclusions this library states:

- **A pass that classifies must not touch the delivery mark.** A reconciliation pass compares a
  snapshot taken at its start against the disk. By the time it writes, a delivery may have finished
  and recorded what it embedded; applying a `Modified` conclusion must update what the scan observed
  — content hash, size — and leave the mark alone. Re-reading immediately before writing is not a
  substitute: the mark can land between the read and the write. The only safe version is not to
  write that column from this path at all.
- **An insert that turns out to be an update must not clear a mark it never set.** A writer states
  `Added` because the index had no record when it looked, and carries no mark for a file it believes
  is new. The row existing proves that belief stale — and the file may already have been delivered.
  Taking the incoming absence over the stored mark un-marks an indexed file, the queued operation
  no longer sees its work as done, and the file is embedded a second time. The hold guarantee above
  is broken by a writer that was only trying to insert.

**A third way exists and is currently unreachable, which is a property of this design rather than
luck.** Preserving a stored mark under conflict is *wrong* when the conflicting row is a different
file: a record read for one path and written under another would take the destination's mark onto
content that was never delivered, the queued operation would retire without embedding, and the file
would be silently absent from every search while each later pass classified it as unchanged. That is
the worse failure of the two above — a duplicate embed wastes a round trip; an unindexed file
answers nothing. It cannot arise here while two properties hold: a record's identity is a pure
function of its relative path, and no writer states a conclusion for one path from a record read at
another. A rename is a removal and an addition, each under its own path. **Anything that adds move
detection re-opens this**, and must then condition the preserve on identity rather than on absence.

The store meets this. A classification never names the delivery mark, so it cannot revert one; an
insert that turns out to be an update keeps the mark it never set; and the mark is cleared in
exactly one case — a row that was not active when the insert conflicted with it.

**That last case is one this design creates for itself, and it is worth naming.** A removal marks a
row rather than deleting it, so the row can still be there when the same path comes back. What the
mark referred to left with the file's chunks when its removal was delivered, so a file restored
byte-for-byte would look already delivered and would never be embedded again — silently, and for as
long as it existed. Avoiding one way to hold a stale mark introduced another; the condition on the
conflict clause is what closes it.

## The vectorization seam, as implemented

`IVectorizationService` is the whole of what crosses back from this library into the application, and the
application implements it in `RagBridgeVectorizationService`: one delivered file in, chunks and vectors
out. It is the per-file counterpart of the corpus pass, and the two share the pieces that must not
diverge — one tokenizer, one chunker, one embed window. Two definitions of how a file becomes chunks
would drift, and the symptom would be a file that retrieves differently depending on which path indexed
it.

**The delivered id is the identity.** Chunks are keyed on the `docId` the library hands over, not on
anything derived on the far side, so a file that is moved and then edited stays one file under one id
rather than becoming two.

**Four things end in writing nothing, and none of them is a failure:**

- **The metadata folder.** The database lives inside the watched folder, so indexing it would make every
  write a change to that folder, and the indexer would never go quiet — each pass triggering the next for
  as long as the process runs.
- **An extension this system does not read.** The question is asked of the scanner rather than answered
  from a second list beside it; two lists would drift silently, and a file indexed by one path and
  ignored by the other looks exactly like a file that was never saved.
- **A corpus-fitted embedder with no stored fit.** A fit is taken against a corpus and one delivered file
  is not one. Embedding anyway would store vectors in a space no query can reach, and no query could
  detect it: such a vector is not malformed, it simply means something else.
- **A delete for something never indexed.** Delivery is at-least-once, so a delete can arrive twice or
  arrive for a file whose upsert was skipped. Treating it as an error would abandon the operation once
  its attempts ran out and mark a file failed for having nothing to remove.

**Deletion removes vectors explicitly, before the row whose cascade takes the chunks.** Under the blob
backend this looks redundant, because the chunk rows cascade and take their vectors with them — and a
test on that backend passes either way. It is not redundant: a native store keeps vectors in a virtual
table, which cannot be a foreign-key target, so there the cascade cannot fire at all and a vector would
outlive its chunk as a hit resolving to nothing.

### The sharp edge: two writers of the file table

The corpus pass and the per-file path both write `file_manifest`, and that is recorded rather than
designed around. A corpus pass rewrites those rows wholesale and deletes any file its scan did not
see, so a pass overlapping a delivery can remove a row the delivery just wrote and take that file's
chunks with it through the cascade. Nothing drives either path yet, so how the two are ordered is
the composition's to settle.

**The store settled one half of this and not the other, and the half it did not is worth stating
plainly.** This spec expected the resolution to be ownership: the indexer owning file rows outright,
the embedding side owning only chunks and vectors. What was built is narrower, because the schema
does not permit the clean version — a file's chunks hang from its row, clearing them means clearing
vectors first, and only the embedding side can do that. So a removal **marks** the row and queues the
delivery; the delivery clears the vectors, and the row goes with them. The indexer owns everything
the row says about a file; the embedding side ends the row's life. That is the arrangement, recorded
in place of the intention it replaced rather than beside it.

## Observability

Every loop in this subsystem is built to survive a fault and keep converging: the reconcile loop
outlives a bad pass, the per-change pipeline outlives a bad change, the dispatcher retries a delivery
and eventually gives up on it. Each of those decisions is right and none of them change here. They
share one cost, and it is the reason this section exists — **surviving a fault and never meeting one
look identical from outside.** A reconciliation that has failed every pass for an hour presents
exactly as one with nothing to do, and either way the first symptom is a search that quietly does not
find a file.

- Each component takes an **optional logger** and runs silent without one. Abstractions only: this
  library records what it survives, and takes the logger to record it with from whoever hosts it
  rather than choosing a logging implementation on its host's behalf. Nothing composes these
  components yet, so there is no one place that hands them a logger; that arrives with the
  composition, and until then each is given one directly or left silent.
- **Logging never changes control flow.** Every survival specified above stays exactly as specified.
  It is recorded, not altered.
- **Level follows what an operator can act on**, not how alarming the exception looks. A file locked
  during a reconcile pass, a hash that lost to a live writer, a delivery that will be tried again, and
  a write-ahead-log checkpoint that could not be taken are **debug** — they are the ordinary
  consequence of indexing a folder somebody is using, and at any louder level an afternoon of editing
  would bury everything else. A reconcile pass that failed, a change the pipeline dropped, a file
  given up on after its attempt limit, and a drain of the outbox that failed are **warning**. A
  delivery abandoned after `MaxAttempts` is **error**, and it is the only error raised here: that
  operation is retired, the file is recorded as failed, nothing will try it again, and its one other
  symptom is a search that does not find a file that is plainly there.

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

The dispatcher runs against an in-memory outbox, a scripted embedding side and a clock the test advances,
so a retry delay is asserted as the value it is rather than slept through. Two orderings need real
concurrency and get it without racing a deadline: one file's operations are shown never to overlap with
four slots available, and two files are shown to run at once by having each delivery wait until the other
has started — run one at a time, the first would time out. The checkpoint rule is asserted by counting
invitations across a real run: one for a burst, none for a dispatcher that never delivered, one more for
each later burst, none while the store is failing every drain, and deliveries continuing past a
checkpoint that throws.

What each loop survives is asserted through a logger that records instead of writing, because the
survival has no other observable: a pass that failed and a pass with nothing to do leave the same
state behind them. The assertions are on the **level** as much as the text, since the level is what
decides whether anyone ever reads the line, and the debug cases additionally assert that nothing
louder was written — that is what keeps an ordinary editing session from being reported as a fault.


Reporting a change needs no filesystem: the host is told a path changed and never looks at it, so
those tests assert the routing and the settling rather than the machine. The exception is the case
the routing exists for and which cannot be staged any other way — a real write and a report racing
each other for the same file, which has to settle as one change. Every assertion that something is
*not* reported is made behind a tracer that is: once the tracer arrives, whatever should have been
refused has had at least as long and did not, so the test cannot pass by waiting alone.

A hold is asserted by first asserting a negative — nothing published while it was open — over
several poll intervals, because a publication that is merely late is indistinguishable from one
that was suppressed if the wait is short. One property is deliberately not demonstrated: releasing
the last hold publishes immediately rather than at the next poll, and the two differ only in
timing, so separating them would mean racing the interval and calling the result a rule. The
reconciler's deferral is asserted against a hold the test switches by hand, since what is being
checked is that the pass asks and obeys, not how a real hold decides.


The store is tested against a real database rather than a fake, because what is under test is SQL:
which columns a statement names, and which it leaves alone. Most of its tests assert what is *not*
written — the delivery mark surviving a classification, surviving an insert that turned out to be an
update, and going when the row it described did — since every one of those failures is silent and
shows up only as a file that is in the folder and not in any answer.
The vectorization seam is tested against a real database, and most of its tests are about what it
refuses, because every refusal is a failure that would otherwise be silent. One of them can only be seen
on the native backend: the explicit deletion of vectors is invisible under the blob store, where the
cascade does the same work, so mutating it away kills nothing there — the test that catches it runs
against `vec0`, and skips where the platform has no binary. The serialisation of concurrent deliveries is
**not** demonstrated: widening the gate leaves every test passing, because the busy timeout absorbs the
contention at this scale, and that is said in the test rather than left looking like coverage.

## Open questions

- Whether the settle loop should poll or schedule per path. Polling is used, because a burst
  touching a thousand files would otherwise schedule a thousand timers to do one pass's work, and
  the poll interval is already bounded by the window it detects. Worth revisiting if a very long
  window makes the latency noticeable.
- Whether a folder moved in or deleted should be expanded into per-file changes on the event path. Today
  its files wait for the next reconcile, which bounds the delay by the reconcile interval rather than by
  the debounce window.
- **Where a hold should be opened.** The mechanism is here and nothing opens one: no part of this
  repository yet knows where a single piece of work ends. Whatever eventually does is what decides
  whether a hold covers a whole edit or only part of one.

## Related specs

- [SPEC-120 — RAG Indexing](SPEC-120-rag-indexing.md)
- [SPEC-130 — Persistence](SPEC-130-persistence.md)
