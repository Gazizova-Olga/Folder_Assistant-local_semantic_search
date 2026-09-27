# SPEC-170 — Conversation Persistence

| | |
|---|---|
| Status | Draft — implemented: the database, the history provider and the session store. The HTTP surface that reads history is not built |
| Version | 0.3.0 |
| Owner | Persistence |
| Last updated | 2026-09-27 |

## Purpose

Keeps a conversation after the process that held it has gone: the turns it is made of, and the
serialized agent session each turn continues from.

## Scope

**In scope**

- The conversation database: where it lives, its schema, and its forward migration path.
- Which seam writes a conversation's messages, and in what form they are kept.
- What a front end may read back about a conversation.

**Out of scope**

- The index database — files, chunks, vectors, the outbox
  ([SPEC-130](SPEC-130-persistence.md)). The two are separate files; see below for why.
- What a turn *is*, how it is routed, and which agent enters it
  ([SPEC-100](SPEC-100-conversation-orchestration.md)).
- The HTTP surface that serves history ([SPEC-100](SPEC-100-conversation-orchestration.md)).

## Implementation status

**Live.** The database is created before the server listens, by one bootstrapper that owns its whole
schema; `ConversationStore` is its only writer, serving two seams over one connection policy — the
messages, through `SqliteChatHistoryProvider` on every agent, and the sessions, behind the
`IAgentSessionStore` the turn already used ([SPEC-100](SPEC-100-conversation-orchestration.md)). A
conversation survives a restart, which is asserted as that: a second store and provider over the same
file continue a conversation the first one wrote. **Not built**: the HTTP surface that would let anyone
read a conversation back, and any pruning of one.

## Requirements

### A second database, never the index file

**Conversation state lives in its own SQLite file in the same metadata folder**, `conversations.db`
beside `manifest.db`. Sharing one file would be fewer moving parts and is refused for two independent
reasons, either of which alone decides it:

- **An index rebuild is destructive and history has to survive it.** Rebuilding drops and repopulates
  the index schema; a conversation is not derived from the folder and cannot be rebuilt from anything.
  In one file, the cheapest recovery from a corrupt index — delete the metadata folder and run again,
  which this project tells an operator to do — would take every conversation with it.
- **The access patterns are opposite.** The index has one writer and many readers, with writes on
  background threads in bursts. Conversation state is written on the **request path**, once per turn,
  and read when a front end asks for history. Under one file those two contend for one write lock, and
  the turn — the thing a person is waiting for — is the side that waits.

The two databases share their **connection rules** and nothing else. Every connection to either is
opened by the one connection factory ([SPEC-130](SPEC-130-persistence.md), concurrency rules): no
shared cache, no pooling, `foreign_keys` and `busy_timeout` per connection, because those are
properties of using SQLite correctly rather than of what a particular file holds. A second connection
string spelled out here is the drift that factory exists to prevent.

### Bootstrap

- The database is created in the metadata folder **inside the analyzed folder**, so a conversation
  travels with the folder it is about.
- The schema is ensured on **every** run, not only at creation, and every statement is idempotent.
- **WAL is set once, at creation.** It is persisted in the file, unlike the per-connection settings,
  and it is what lets a front end read history while a turn writes.
- Bootstrap runs **before the server accepts a request**, ordered by the same `IStartupFilter` as the
  index database's. A conversation store that had to create its own schema on first use would pay a
  round trip on the request path and would need a write-capable connection to read.
- `Created` reports whether **this call** inserted the version row, not whether the file was absent
  beforehand: two callers starting together both find no file, and only one of them inserts.

### The schema is created whole

The tables are created together rather than grown one per feature, and the reason is the same one the
index database has: an invariant that cannot be retrofitted. `message.seq` is allocated from
`conversation.next_seq` (below), so the counter has to exist from the first row written — a schema that
grew the counter later would have to reconstruct it from rows already there, and the only available
reconstruction is `MAX(seq)`, which is precisely what the counter exists to avoid.

### The framework's history seam writes the messages, not a projection of our own

**A conversation's messages are written by a `ChatHistoryProvider`** — the agent framework's own
abstraction for an agent whose service does not keep history — set on the agent through
`ChatClientAgentOptions.ChatHistoryProvider`. The framework asks it for history before each invocation
and hands it the request and response messages afterwards.

The alternative considered and rejected was a delegating agent of ours capturing each turn into rows
kept for display, with the session blob remaining the conversation's memory. It would have meant **two
copies of one conversation**: the messages the model is given, inside the framework's session, and the
messages a person is shown, in our rows. Nothing would fail when they diverged — a turn the projection
missed, or rendered differently from what was actually sent — and a reader would have no way to tell.
The default provider already keeps history inside the session, so choosing the seam moves that storage
rather than adding a layer: afterwards there is one copy, in rows.

**What this costs is that the store is on the prompt path.** These rows are what the model is given on
the next turn, so a write that loses part of a message changes what the model believes was said — not
merely what a page displays. Two rules follow, and both are in the data model below: a row holds the
framework's serialized message rather than text, and the display form is derived when it is read and
never stored.

**A provider instance serves every session, so it holds no session state of its own** — the framework's
contract says as much, and directs per-session state into the `AgentSession`'s state bag. What this
provider needs is the conversation it is writing for, which is this application's identifier and not the
framework's: the invocation context carries the agent and the session, and a chat-completions service has
no conversation id of its own. So **the turn names the conversation on the session** before it runs, under
one key, and the provider reads it from there. A run with no conversation named — a delegation, which the
registry runs with no session at all — **reads nothing and writes nothing**, which is what keeps a
delegate's request out of the conversation that asked for it.

**Two properties of the seam were measured against the framework rather than read from its**
**documentation**, because the documentation states the first of them two ways:

- **What is handed back for storage is the caller's new messages only** — not the history the provider
  supplied moments earlier. So a turn appends its request and response messages and each message is
  stored once. Had it been the accumulated set, the naive write would have re-stored the whole
  conversation every turn and grown it quadratically, with nothing failing.
- **With a provider set, the session carries no messages.** The framework's default provider keeps them
  in the session's state bag; ours keeps them in rows, and what the session then carries is the
  conversation's name and whatever else a session holds. That is the *one copy* claim, and it is asserted
  by serializing a session after a turn and finding the question absent from it.

### Data model

| Table | Holds |
|---|---|
| `schema_version` | One row. The version this database was created at |
| `conversation` | One row per conversation, with the sequence counter its messages are numbered from |
| `message` | One row per message in a conversation, in order, as the framework serialized it |
| `session_state` | One row per (agent, conversation): the serialized agent session |

**`conversation.next_seq` is the allocator, and `MAX(seq) + 1` is forbidden.** A message's sequence is
taken by advancing the counter inside the same transaction that writes the row (`UPDATE … RETURNING`).
Two turns of one conversation are not ordered against each other
([SPEC-100](SPEC-100-conversation-orchestration.md)), so two writers reading `MAX(seq)` see the same
value and write the same sequence: a unique constraint then fails one turn that had already succeeded,
or — without the constraint — two messages claim one position and the order a reader sees is arbitrary.
The counter is the one value both writers contend on, and the transaction is what makes contending on it
correct.

**A row holds the framework's serialized `ChatMessage`, not display text.** A turn is not only words: a
tool call, its result and a refusal are all messages, and they are what the next turn's context is made
of. Text would drop them, and the loss would show as a model that had forgotten it called a tool rather
than as an error. A front end renders these rows to text when it reads them; that rendering is never
stored, because two spellings of one message can disagree about what the model saw.

**A message names the agent that produced it**, from the provider's own invocation context rather than
from anything fixed at construction: one roster holds several agents, a delegating roster answers one
conversation through more than one of them, and a provider instance serves them all.

**`session_state` is keyed by agent *and* conversation.** Two agents serving one conversation hold two
sessions, and a key of the conversation alone would have each overwrite the other's — the same rule the
in-process store already holds ([SPEC-100](SPEC-100-conversation-orchestration.md)).

**The rows are the history; the blob is whatever else a session holds.** Once the provider writes the
messages, the session carries routing state, tool-approval state and whatever a context provider keeps —
not the conversation. So a reader never has to open the blob to answer what was said, and the two are not
two copies of one thing.

**An unreadable session blob starts a fresh session**, and leaves the message rows alone. One blob that
cannot be deserialized — a framework upgrade that no longer reads what the last one wrote — must not end
every later turn of its conversation. The messages are unaffected, so what is lost is the rest of the
session and not the conversation.

**An unreadable message row fails the turn, and that asymmetry is the point.** Skipping it would hand the
model a conversation with a hole in it, which it would answer as though the missing turn never happened —
the silently plausible wrong answer this system exists to refuse. The failure names the conversation and
says the conversation can be deleted to start again, because that is the one action available. Losing a
session blob costs memory; losing a message changes what was said.

### Error model

- An invalid path or configuration is an argument error, thrown before anything is opened.
- A failure to open or initialise the database propagates and stops startup, exactly as the index
  database's does. A process that starts without somewhere to keep conversations would lose them
  silently, one turn at a time.

## Configuration

| Key | Default |
|---|---|
| `FolderAssistant:Persistence:ConversationDatabaseFileName` | `conversations.db` |

The metadata folder and the analyzed folder path are [SPEC-130](SPEC-130-persistence.md)'s; this
database is in the same folder by construction, because it is *that folder's* conversations.

## Non-functional requirements

- **Reliability** — startup always guarantees the tables exist; an existing database is reused and
  missing objects are created without destructive change.
- **Durability** — a turn that succeeded is on disk before the next one starts.
- **Concurrency** — one file, WAL, written on the request path and read by whoever asks for history;
  the connection rules are [SPEC-130](SPEC-130-persistence.md)'s.
- **Operability** — bootstrap reports whether it created or reused the database, and where.

## Test strategy

The bootstrap is asserted against a real database in a temporary folder: the file appears where the
configuration says, `Created` is true once and false on every later call, every table and the columns
each writer depends on are there, and `journal_mode` reads `wal` on a connection that did not set it —
which is what proves it was persisted rather than applied per connection.

**The split from the index database is asserted as the property it exists for**, not as two file names:
a conversation written, the index database then deleted and re-bootstrapped as recovery from a corrupt
index would, and the conversation still readable afterwards. Two paths compared would pass while both
pointed at one file through different configuration.

## Open questions

- **Nothing resumes a stored conversation yet, and that is the visible half of what is missing.** The
  console names a new conversation on its first turn of every run ([SPEC-100](SPEC-100-conversation-orchestration.md)),
  so what is written is never read back by the application that wrote it — the rows are there, and only a
  SQLite client can see them. Resuming is the HTTP surface’s to offer, and a front end that resumed the most
  recent conversation by default would be a decision about what an operator expects, not a missing wire.

- **Whether a conversation is ever pruned.** Nothing ages a conversation out today, and an operator's
  only control is deleting the file. A retention rule needs to know what a conversation costs in
  practice before it can choose a bound, and the index's outbox pruning is the shape to copy when it
  does ([SPEC-130](SPEC-130-persistence.md)).
- **Whether the provider's rows should be compacted, and by what.** The framework ships compaction
  strategies (`Microsoft.Agents.AI.Compaction`) that a provider may apply as history grows, and nothing
  here applies one: a conversation's whole history goes to the model. The bound is the provider's to
  choose and it chooses none, which is honest for a local single-user tool and wrong for a long-lived
  one. Measure a real conversation's growth before picking a strategy.

## Related specs

- [SPEC-100 — Conversation Orchestration](SPEC-100-conversation-orchestration.md)
- [SPEC-130 — Persistence](SPEC-130-persistence.md)
- [SPEC-900 — Versioning and Migration](SPEC-900-versioning-and-migration.md)

## Changelog

- **0.3.0** (2026-09-27) — built: `ConversationStore` as the database's one writer over two seams, the
  `SqliteChatHistoryProvider` on every agent, and the session store replacing the in-process one behind the
  seam the turn already used. A conversation now survives a restart. Two properties of the framework's seam
  were measured rather than assumed — that storage is handed the caller's new messages only, so nothing is
  stored twice, and that a session with a provider set carries no messages, so the rows are the only copy.
  The turn names the conversation on the session, since the provider serves every session and can hold none
  of its own; a run with no conversation named keeps no history, which is what a delegation is. An
  unreadable message row fails the turn while an unreadable session blob starts a fresh session, and the
  asymmetry is written down: losing a blob costs memory, losing a message changes what was said.
- **0.2.0** (2026-09-27) — the framework's `ChatHistoryProvider` is what writes a conversation's messages,
  decided before anything was built on the alternative: a projection of our own would have been a second copy
  of one conversation, diverging from what the model was actually given with nothing failing. So a row holds
  the serialized `ChatMessage` rather than display text, the display form is derived on read, and the session
  blob keeps what is left of a session rather than the history. The `message` table changed shape with this,
  in the same commit that created it. The open question about text versus content parts is answered;
  compaction replaces it.
- **0.1.0** (2026-09-27) — written with the conversation database and its bootstrapper: a second file
  beside the index for two independent reasons, the whole schema owned by one bootstrapper and ordered
  before the server listens, WAL once at creation, `conversation.next_seq` as the sequence allocator
  with `MAX(seq) + 1` refused, a message naming the agent version that produced it, `session_state`
  keyed by agent and conversation, and history read from rows rather than from the session blob. No
  store exists yet; the two open questions above are the store's to answer.
