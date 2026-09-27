# SPEC-121 — File-Indexing Front End

| | |
|---|---|
| Status | Draft |
| Version | 0.22.0 |
| Owner | Indexing |
| Last updated | 2026-09-27 |

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

**Live.** Every seam this library defines has an implementation, the library composes its own loops
(`FolderIndexer`, behind `IFolderIndexer` — see Composition), and the application constructs the
store, the vectorization seam and the indexer together and starts it once its whole-folder pass has
succeeded ([SPEC-120](SPEC-120-rag-indexing.md), Keeping up with the folder). The sections below
describe only what exists.

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
- **An extension this system does not read**, given the list of the ones it does. The alternative is
  recording and delivering every binary in the folder to a consumer that refuses each one — churn on
  every pass rather than a fault. The list is the host's to supply, because only the application
  knows what it can read; without one, every extension is reported.
- **A file over the size bound.** Size is a property of a file on disk rather than of a path, so it is
  decided by the two writers rather than at the watcher: a file over the bound is neither hashed nor
  recorded, and one the index already holds is removed. A file that grows past the bound leaves the
  index, and one that shrinks back is added again.

Exclusion matches a **whole path segment**, never a prefix. A folder called `binaries` or `objects`
is an ordinary folder. Every walker over the folder — the watcher, the reconciler, the per-change
path — asks **one instance** of the rule, so they cannot disagree about what the corpus is.

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
one entry and is published once. The application holds at the agent-run boundary
([SPEC-100](SPEC-100-conversation-orchestration.md)): every agent run, a delegate's nested inside its
caller's, is one batch.

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

**The walk skips a subtree it cannot enter, and does not count it.** A folder that disappears
mid-walk is ordinary and must not end the enumeration for everything after it, and the same
setting is what skips a folder that merely denied access — the two look identical from here. The
recorded files under either are therefore classified as removed, queued, and cleared, until a later
comparison can enter the subtree again and records them as added. That is the one way a pass
removes what is still on disk, and it is recorded here rather than closed, because closing it means
telling a vanished folder from a forbidden one and the walk does not.

Hashing parallelism is sized independently of anything else. It is a disk- and CPU-bound job that
scales with the machine, unlike delivery onward, which is one round trip per file into a single
backend. A single knob for both could only ever suit one of them.

**Change detection uses SHA-256 over the file's bytes — the digest the application's corpus scanner
computes for the same file, over the same bytes.** Change detection alone would be served by a faster
hash, since the only question is whether these bytes differ from the last ones seen. What decides it
is that the whole-folder pass embeds a file only when the hash its scanner took equals the one this
library recorded ([SPEC-120](SPEC-120-rag-indexing.md), Delta handling). Hashing differently would not
fail: every file would be deferred, the pass would embed nothing, and the deliveries would do the whole
corpus one file at a time — correctly, at full cost, visible only as a pass reporting every file
deferred. It is over the bytes rather than the decoded text because decoding drops a byte-order mark
and replaces what it cannot read, so the two would disagree on exactly those files. The
content-addressed chunk id is also a SHA-256; the two share an algorithm and nothing else — one keys
stored rows, the other answers whether a file changed.

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
and write time that hold still across an interval. The denial is a Windows property: on Linux share
modes are advisory, a held file opens, and the second look is the only one of the two checks that
can see a writer — a writer holding a file whose size and write time do not move across the
interval is not seen there at all, and the content hash at the next reconcile is what corrects it.

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

**A removal is delivered only while the file is still gone.** The store marks a removed file's row and
queues its removal; a file that comes back before that removal is delivered is recorded active again, its
mark cleared, with the upsert that re-embeds it queued behind the removal. So before delivering a removal
the dispatcher asks the store whether the file is recorded, and retires the operation untouched when it
is. Delivered anyway, the removal ended the row the file had since taken again and took its chunks with
it; the upsert behind it found nothing recorded and skipped; and the file was absent from every search
until a periodic pass rediscovered it — plausible during a long startup backlog, and silent throughout.

**And the row is ended only while it is still marked gone.** A return landing after the dispatcher's
question and before the write is a window the question cannot close, one store round-trip wide rather
than an embed, with exactly the failure above at the end of it. So the side that ends the row asks again,
in the transaction that ends it: the status it reads is the status the delete acts on, and a return either
precedes that transaction or waits for it. Asking twice is not a redundancy — the dispatcher's question is
what makes an overtaken removal cost nothing, and this one is what makes a removal that is already being
delivered safe. Neither can be dropped in favour of the other, and the condition lives with the write
because nothing outside a transaction can hold it ([SPEC-130](SPEC-130-persistence.md)).

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

**A delivery that never returns is a failed delivery, not a delivery in progress.** Nothing here bounds it
— the dispatcher awaits the seam — so the bound is the embedder's own deadline
([SPEC-162](SPEC-162-embedding-ollama-local.md), a bounded call), and it has to be there: a call that never
returned held its operation in flight forever with attempts still at zero, so the retry, the backoff and
the attempt limit above never engaged, and nothing reported it. It surfaces as a timeout, not a
cancellation, so the attempt is counted like any other failure rather than mistaken for the host stopping.

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

**At that same moment, and before the checkpoint, the dispatcher invites the store to discard the record
of deliveries that succeeded.** Without it the outbox grows one row per change for the life of the
folder: a queue that only ever accumulates is not a queue, and every scan of it gets slower while
everything it holds is work that arrived. The rules are the checkpoint's, with two of its own:

- **Only operations that succeeded.** A delivery abandoned after `MaxAttempts` is the record that a file
  is *not* in the index, and the only other symptom is a search that quietly does not find it, so a
  failed row is kept however old it is. Pending and in-flight rows are work.
- **Older than a retention window**, `DeliveredRetention`, one day by default and `TimeSpan.Zero` to keep
  everything. The window is for a person: whoever is working out what a burst did still has the rows.
  Measured from when the operation was **queued**, because that is the only time the table records — so
  an operation that spent a day being retried and then succeeded is eligible at once, which is the row
  someone would most want to read. Making the window mean *since delivered* costs a column and a
  migration ([SPEC-130](SPEC-130-persistence.md)) and is worth it only if these rows turn out to be read
  for diagnosis in practice.
- **Before the checkpoint**, so the space a prune frees is what the checkpoint then reclaims rather than
  space the next burst waits for.
- **Advisory, like the checkpoint.** An outbox carrying rows it no longer needs costs disk, never
  correctness, so a prune that fails is logged at debug and the loop goes on — and the checkpoint behind
  it still happens.

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

**And there is one writer of what the row says.** The whole-folder pass records the folder through
this library's own comparison before it embeds anything, and writes only chunks, vectors and —
through the store's conditional mark — what it delivered; the per-file delivery writes chunks and
vectors under the id it was handed, and nothing about the row. Both used to write the row too, and
the delivery's version of that was the race this rule exists to name: a hash read before a slow
embed, written back afterwards over the newer one the front end had recorded meanwhile — after
which the delivery's own conditional mark matched the reverted row, the delivery queued for the
newer content found its work already done and skipped, and the file sat on stale vectors until a
periodic pass happened to re-hash it. Silent throughout. A chunk row references the row the store
wrote, so the ownership is held by the schema rather than by discipline: chunks for an id no row
carries fail on the foreign key.

## The vectorization seam, as implemented

`IVectorizationService` is the whole of what crosses back from this library into the application, and the
application implements it in `RagBridgeVectorizationService`: one delivered file in, chunks and vectors
out, under the id it was handed, and nothing about the file's record. It is the per-file counterpart of
the corpus pass, and the two share the pieces that must not diverge — one tokenizer, one chunker, one
embed window. Two definitions of how a file becomes chunks would drift, and the symptom would be a file
that retrieves differently depending on which path indexed it.

**The delivered id is the identity.** Chunks are keyed on the `docId` the library hands over, not on
anything derived on the far side, so a file that is moved and then edited stays one file under one id
rather than becoming two. The row that id names was written by the store before the delivery was
queued; the delivery does not write it and could not.

**Three things end in writing nothing, and none of them is a failure:**

- **The metadata folder.** The database lives inside the watched folder, so indexing it would make every
  write a change to that folder, and the indexer would never go quiet — each pass triggering the next for
  as long as the process runs.
- **An extension this system does not read.** The question is asked of the text extraction registry
  ([SPEC-120](SPEC-120-rag-indexing.md)), the one source the scanner walks with, rather than
  answered from a second list beside it; two lists would drift silently, and a file indexed by one
  path and ignored by the other looks exactly like a file that was never saved.
- **A delete for something never indexed.** Delivery is at-least-once, so a delete can arrive twice or
  arrive for a file whose upsert was skipped. Treating it as an error would abandon the operation once
  its attempts ran out and mark a file failed for having nothing to remove.

**One thing ends in a failure, and used to end in writing nothing: a corpus-fitted embedder with no
stored fit.** A fit is taken against a corpus and one delivered file is not one, and embedding anyway
would store vectors in a space no query can reach — such a vector is not malformed, it simply means
something else. But returning normally was worse than either: the dispatcher recorded the file as
delivered for content that was never embedded, and it was absent from every search while the record
said otherwise, healing only on a restart. So the seam throws, the operation is retried and retired as
failed at error level where it can be seen, and the file carries no mark. It is reachable on the
`lsa-*` profiles when the whole-folder pass ran over an empty folder — it fits nothing then — and
files arrive afterwards; only the next start's pass fits, which is the open edge this leaves.

**The write is serialised; the embed is not.** The seam's gate sits around the repository write alone,
because the embed is a round trip into a backend that may well take several at once, and a gate around
it would make the dispatcher's parallelism measure the gate rather than the backend. The fit is restored
once, under its own lock, because the restore replaces the analyzer and the model together.

**Deletion removes vectors explicitly, before the row whose cascade takes the chunks.** Under the blob
backend this looks redundant, because the chunk rows cascade and take their vectors with them — and a
test on that backend passes either way. It is not redundant: a native store keeps vectors in a virtual
table, which cannot be a foreign-key target, so there the cascade cannot fire at all and a vector would
outlive its chunk as a hit resolving to nothing.

### One writer of the file table, and how the whole-folder pass fits

The corpus pass and the per-file path used to both write `file_manifest`, kept apart only by order,
and that arrangement is gone. The whole-folder pass ([SPEC-120](SPEC-120-rag-indexing.md), Delta
handling) now runs this library's own comparison over the folder before it reads a file, into the
store: every row it will embed against was written by the store, from the same classifier the loops
use afterwards. The pass then embeds what the record says lacks vectors, at exactly the content it
read, writes chunks and vectors through the repository, and reports what it embedded through the
store's conditional mark — after the vectors have committed, and under the same condition a
delivery marks under. The deliveries its comparison queued find their work done and skip. A file
the comparison did not record, or recorded at other content than the pass read, is left to the
delivery the front end holds for it.

Two consequences follow. A delivery left queued when the process stopped is no longer embedded
twice: the next start's pass embeds it and marks it, and the queued delivery skips. And the loops
still must not run while the pass does — not because of a second writer any more, but because the
pass embeds against a snapshot of the record and the loops move the record.

**The row's life still ends on the embedding side, and the reason has not changed.** A file's chunks
hang from its row, clearing them means clearing vectors first, and only the embedding side can reach
a native store's vectors. So a removal **marks** the row and queues the delivery; the delivery clears
the vectors, then the row, whose cascade takes the chunks. The store writes everything the row says
about a file; the embedding side writes nothing to it and ends it once, and only while the row still
says the file is gone — the condition above, which is the one thing that side reads from the row rather
than being told. The pass deletes nothing for the same reason.

## Composition

`FolderIndexer` owns the four loops and runs them together, behind `IFolderIndexer`; it is the whole
of the library's surface to a host. Each loop is correct alone and useless alone — the watcher
publishes to a channel nothing reads, the dispatcher drains a queue nothing fills — so the one type
that knows all four is where the things they share are decided:

- **One filter.** The watcher, the reconciler and the per-change path decide what is in the corpus
  with one instance, built once from the metadata folder name, the extension list and the size bound.
- **One hold.** The reconciler is handed the watcher as the hold it defers for, so a periodic pass
  waits for the same batch the watcher is holding back. Built without that introduction, both would
  be correct on their own and the hold would cover nothing that reaches the store through the safety
  net.
- **One order of starting.** The folder is compared against the index before anything else runs: the
  watcher sees only what happens after it attaches, and a folder edited while nothing was running has
  changes nothing will ever report. The loops start afterwards, so the outbox already holds that
  pass's deliveries when the dispatcher first looks. That first pass is not caught — a folder that
  cannot be read at all is the host's to hear about, where the periodic pass survives a bad one
  because it has a next one.
- **One order of stopping, and the order is the guarantee.** The watcher goes first and hands over
  whatever was still inside its quiet window; the pipeline then finishes recording those changes,
  durably; only then are the loops that stop on cancellation told to. Cancelling everything at once
  would drop the settled change the pipeline was about to record, and the file would stay stale
  until a reconciliation happened to notice. The wait is bounded by the host's token, never cut short
  by the library: a host out of patience cancels it, and what was not recorded by then is the next
  start's pass to find — which is said, at warning.

While nothing is running, a report is dropped and a hold holds nothing, for one reason: a change made
while nothing is watching is what the pass at the next start is for.

**The application starts it once its whole-folder pass has succeeded, and stops it with the host.**
The start is part of the same attempt as the pass — an attempt whose front end could not start is
failed and repeated whole, rather than leaving a ready index that has quietly stopped following the
folder. The pass runs once and never again while the front end runs, because it embeds against a
snapshot of the record and the loops move the record (one writer of the file table, above).

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
  a write-ahead-log checkpoint or an outbox prune that could not be run are **debug** — they are the ordinary
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
checkpoint that throws. The prune is counted the same way, and the **order** with it: one prune per burst,
the prune before the checkpoint, none for a dispatcher that never delivered, none at all at a retention of
zero, and a prune that throws followed by its checkpoint with nothing logged above debug. What a prune
drops is asserted against the real store instead, which is where it can be: an old delivered row goes, a
recent one stays, a failed one stays however old, and pending or in-flight work is not a candidate.
Pruning failures as well as deliveries fails that one test — the row whose absence is a file silently
missing from every search.

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
against `vec0`, and skips where the platform has no binary. The serialisation of concurrent writes is
**not** demonstrated: widening the gate leaves every test passing, because the busy timeout absorbs the
contention at this scale, and that is said in the test rather than left looking like coverage.

The two ways a delivery ended in the wrong state are asserted through the real dispatcher, on the outbox
row an operator would read: a generator that never answers ends as an operation retired failed within
the embedder's deadline, with `TimeoutException` recorded against it and the file unmarked; and an
unfitted corpus-fitted embedder ends the same way, with the model named in the error and nothing
written under the file.

A removal overtaken by the file's return is asserted at each point it can be overtaken at. Against the
in-memory outbox, with the return recorded before the delivery: the removal is retired without reaching
the embedding side, and the upsert queued behind it is delivered. Against the real store and bridge, same
order: the file's row and chunks are there afterwards with its mark set, which is the assertion the
defect failed, since the delivered removal ended the row. And with the return recorded **from inside the
delivery** — where a settled change or a reconciliation pass would land it, after the dispatcher's
question — the row keeps its id and its chunks, and the upsert the return queued delivers behind it. That
last one is mutation-tested at both levels, by weakening the condition to *any row exists*: it kills the
delivery test and its unit counterpart, which asserts the vectors are left alone as well as the row.

The ownership of the file table is asserted against a real store and a real dispatcher, in three parts.
A record that moves on while a delivery holds its embed is not reverted by that delivery, its stale
mark is refused, and the delivery queued for the newer content is the one that embeds it. A pass over a
fresh folder leaves every row under the id this library derives, with its creation time and its
delivery mark, and a drain afterwards embeds nothing. And the repository refuses chunks for an id no
row carries, on the foreign key. The first two are mutation-tested — the write-back restored, the mark
removed — and each is caught by the test written for it and by no other. The in-flight edit is staged
on the record rather than on the disk, because the delivery holds the file open share-Read and a
writer is denied on Windows; the record is what the old write-back reverted, so it is the record that
has to move.

The composition is tested against a real store and a real folder, because what it decides only shows
when the loops run together: that a change reaches the embedding side with nothing calling any loop;
that stopping records a change still inside its quiet window, staged through the write-through entry
point so the change is in the debouncer for certain rather than an event still in flight; and that a
periodic pass waits for a hold opened on the indexer, asserted with a quiet window long enough that
only that pass could have recorded the file — the test that fails if the two are built without being
introduced. The index's own database writes, which land inside the watched folder while the loops
run, are asserted never to have been delivered.

Change detection is asserted to hash the bytes — a byte-order mark is part of what is hashed — and to
agree with the application's corpus scanner on the same file, in a test that runs the scanner, so a
change to either side fails there. The extension and size rules are asserted as functions and then
through each writer: the reconciler neither hashes nor records a file outside the list, removes a
recorded file that has grown past the bound, and counts it as neither skipped nor present; the
per-change path removes such a file when its change settles.

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
