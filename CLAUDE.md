# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

**Folder Assistant** — a local-first LLM agent that indexes a folder on disk, exposes a confined
set of filesystem tools over it, and answers questions about its contents using
retrieval-augmented generation. A single ASP.NET Core application on .NET 10, built towards the
Microsoft Agent Framework (`Microsoft.Agents.AI.*`) and `Microsoft.Extensions.AI`.

The design goal is that the whole loop can run **offline**: embeddings from a local Ollama model
or an in-process embedder, vectors in a folder-scoped SQLite database, retrieval by cosine
similarity or native `sqlite-vec` k-NN. No document text leaves the machine unless the operator
points the agent at a hosted chat provider.

The failure mode the whole design guards against is **a silently plausible wrong answer**. Every
"fail loudly" rule below — index not ready → refuse; unknown profile → startup failure; empty
scan → throw — descends from it.

## Status

Developed on its own since 2026-09-12: there is no reference tree, and nothing here is carried in
from anywhere. `main` is pushed to the private `origin`; plain pushes of `main` and branch + PR
are both in use.

The tree is in three states, and the middle one is the one to watch
([docs/diagrams/implementation-status.md](docs/diagrams/implementation-status.md)):

- **Live** — the whole-folder indexing pass at start; the file-indexing front end
  (`src/FolderAssistant.Indexing`) keeping the index in step with the folder afterwards; the index
  database; embedding and retrieval telemetry served at `GET /metrics`.
- **Built but unreachable** — retrieval (`IRetrievalQuery`, two backends) and the context reducer
  are composed in `Program.cs` and called by nothing on the request path. Every vector written is
  one the running application never reads. The containment guard (`WorkspacePathGuard`), the read
  tools and text search over it (`ReadTools`), the mutation tools over a second guard
  (`MutationTools`) and the file-level semantic search over the wrapped query (`SearchTools`, all
  `SPEC-101`) are built, tested and registered, and nothing resolves them. The provider client
  and the agent over it (`ProviderClientFactory`, `AgentFactory`, `AgentHandle`; `SPEC-140`,
  `SPEC-100`) are built the same way, and so are the tool reflection and the facade (`ToolReflection`,
  `ToolFacade`, `ToolSet`): the one agent holds every tool the three holders have, each under its
  group's failure contract, and is registered and resolved by nothing. Nothing runs a turn.
- **Not built** — the roster and the turn runner, the chat surface, the conversation database. The
  parts of the architecture below that describe them are intent.

Scope and sequencing are owned by `notes/DEVELOPMENT-PLAN.md`. `notes/` is a **separate private
repository** cloned inside this one and gitignored here; nothing in it is ever `git add`ed. Read the
plan before deciding what to build next or what to leave out — it is the authority on scope, not
this file, and its §5.1 lists the known defects in this tree with the order they are to be fixed in.

Its working rules bind here:

- **No `git commit` and no `git push` without explicit approval for that specific action.** Do the
  work, show the proposed message and a diff summary, wait. Nothing is left *out* of a commit
  without approval either — a step ships its spec, diagram and README changes with its code.
- One commit identity; `origin` is the only remote. A pre-push hook installed from
  `notes/tooling/` checks tracked content, messages, identities and remotes; never `--no-verify`
  past it.
- Commit messages say what changed and why, in the author's voice — no ticket ids, no review
  references.

## Spec-first delivery

`AGENTS.md` points at the one canonical skill,
[.agent/skills/spec-alignment/SKILL.md](.agent/skills/spec-alignment/SKILL.md). Before implementing
anything that can change behaviour, a data model, an interface or a workflow: find the affected
spec in `docs/specs/`, state *aligned* / *partially aligned* / *not aligned*, and on a conflict
**stop and name the requirement by spec and line**, offer both options — satisfy the spec, or
change it deliberately — and wait. An approved deviation moves the spec **in the same commit** as
the code; a spec that lands a commit later was wrong in between.

Placeholder specs (`SPEC-140`, `SPEC-163`, `SPEC-900`, `SPEC-920`, `SPEC-930` say so) impose
nothing; a change that touches one writes the requirement into it before the code. A spec reads as
*what is true now*, with its changelog at the bottom.

## Commands

From the repository root:

```bash
dotnet build --no-incremental -v q --nologo     # expect: succeeded, 0 warnings — enforced
dotnet test --nologo -l "console;verbosity=detailed" > "$TEMP/run.txt" 2>&1; tail -3 "$TEMP/run.txt"
dotnet run --project FolderAssistant            # web host + indexer, over the working directory
```

Filtered test runs:

```bash
dotnet test --filter "FullyQualifiedName~FolderIndexingPipelineTests"
dotnet test --filter "DisplayName~Cascades"
```

**Run the suite to a file.** Never `-v q`, never pipe it through `grep`: both discard the stack
trace of a flake, and a re-run that passes takes the only copy with it.

**Ollama.** The `ollama-*` profiles and `OllamaLiveIntegrationTests` need a running Ollama with
`ollama pull qwen3-embedding:0.6b`. Without it those tests — and `SqliteVecBackendTests` without
the native binary — return early and **report green while asserting nothing**; xUnit 2 has no
dynamic skip. A green run proves nothing unless you know what it covered.

**Opt-in benchmarks** are measuring instruments under `[Fact]`; they return immediately unless
their variable is set and assert nothing on purpose:

```bash
BENCHMARK_FILES=4000 dotnet test --filter "FullyQualifiedName~CorpusBenchmark" -l "console;verbosity=detailed"
RUN_SEMANTIC_BENCHMARK=1 dotnet test --filter "FullyQualifiedName~SemanticSearchBenchmark"   # needs Ollama
RELEVANCE_FLOOR_BENCH=1 dotnet test --filter "FullyQualifiedName~RelevanceFloorBenchmark"
```

`BENCHMARK_CORPUS=<path>` points the corpus benchmark at a real folder. Results live in
`docs/benchmarks/`. **Never carry a number forward** — a commit stating a figure re-measures both
sides on the tree it produces, and compares trees from the same kind of directory.

**CI** ([.github/workflows/ci.yml](.github/workflows/ci.yml)) builds and tests in Release on both
`ubuntu-latest` and `windows-latest` for every push and PR to `main`. Development is on Windows, so
the Linux job is where path-separator and case-sensitivity mistakes surface; the Windows job runs
the tests whose subject exists only there (`subst` drives, hard-link names) instead of skipping them.

NuGet versions are managed centrally in `Directory.Packages.props` (Central Package Management) —
**do not put versions in `.csproj` files**. `NuGet.config` maps every package to nuget.org and
nothing else.

`SonarAnalyzer.CSharp` runs as a global analyzer on every build, and **the build fails on any
warning** — `TreatWarningsAsErrors` in `Directory.Build.props`. The baseline is zero and it is
enforced, not documented, so there is no count to remember and nothing to check by hand.

Four warnings are deliberate. They are carried as targeted `[SuppressMessage]` attributes with
written justifications on the members themselves, and listed together in
[docs/ANALYZER-WARNINGS.md](docs/ANALYZER-WARNINGS.md). **A new suppression needs a justification
saying why the code is right**, not why the rule is inconvenient; if that cannot be written, the
warning is correct and the code should change. Never silence a rule repo-wide to get a build green.

The gate exists because a real defect has already hidden in the noise here — a `Split` overload
mis-binding in the watcher's metadata-folder guard, in a fully green build that no test caught.

## Architecture

Invariants first; each section says whether it describes code or intent. The reasons are written
down because every one of these is easy to "simplify" away by someone who does not know why it is
there.

### Composition root — live

`Program.cs` is a composition root, not a script: it registers factories and lets the host
resolve them in order. Configuration binds **lazily through `IOptions<T>`**, never eagerly
off `builder.Configuration` — an eager bind freezes values before the host is built and
silently discards any configuration source added afterwards, including the one a test host
injects. Only the listen port may be read early, because it cannot be late-bound. Database
bootstrap is ordered before the server listens by an `IStartupFilter` taking it as a constructor
dependency — it cannot go at the end of `Main`, because a test host intercepts at `Build()`.

A **composition profile** (`FolderAssistant:Profile`, `CompositionProfiles.cs`) bundles the
vectorizer, the vector store reader/writer and the retrieval strategy **together**, so a mismatched
combination — a lexical query against a neural-embedded store, or a reader looking in `chunk_vector`
while the writer fills a `vec0` table — cannot be expressed. A **named** profile that is unknown or
platform-unavailable is a startup failure, never a fallback. The names are `ollama-vec` (default),
`ollama-blob`, `lsa-vec`, `lsa-blob`, `programmable-vec`, `programmable-blob`: `-blob` stores
vectors as BLOBs scored by cosine in managed code; `-vec` uses the native `sqlite-vec` extension,
which ships no binary for win-arm64 or musl. **The one fallback there is applies to the unnamed
default alone, is a store fallback, and is never silent**: where `ollama-vec` cannot load,
`ollama-blob` runs — same embedder, same embedding space, slower store — logged at warning and
reported by `GET /` with a note (`DefaultResolution`, `SPEC-000`). **A fallback never changes the
embedder.** The default needs a local Ollama holding the model; when it is absent the startup probe
fails the index with a message saying so and searches refuse until it is there. Nothing in-process
runs in its place, because a substitute embedder answers from a different embedding space with no way
for anyone to tell. The default was `lsa-vec` for one day (2026-09-15, on a benchmark re-run) and
became `ollama-vec` on 2026-09-16 by the owner's decision, with every profile re-measured that day on
this tree on both Windows and Linux (`docs/benchmarks/`, and `docs/benchmarks/linux/` for the second).
`lsa-vec` is the profile to name where nothing can be installed; it retrieves far better than the
placeholder and well short of the pretrained model.

Two consequences of the corpus-fitted profiles are known and recorded in `SPEC-161`: the fit is taken
once and kept, so a folder that grows a great deal keeps its early fit until the metadata folder is
deleted (a refit policy is the next embedding item); and an empty folder cannot fit, so its index
reports `Failed` and is retried on the interval until the folder has text. A corpus below the pruning
floor keeps its whole vocabulary rather than failing. `lsa-*` persists under its own model version
(`Indexing:LsaModelVersionId`), never the placeholder's.

One vectorizer instance is shared by indexing and retrieval: a corpus-fitted embedder must be
fitted identically on the write and the query side.

Three embedders exist behind that seam. The **programmable** one is a character histogram — a
deterministic floor with no semantics. The **LSA** one is fitted to the corpus and captures weak
synonymy within it (rank `k` well below corpus rank; reduction scaled `1/√λ`, not `1/λ`, which
cancels `Σ` out — `SPEC-161`). The **Ollama** one (`SPEC-162`) is a real pretrained model served
locally, and is the only one that can retrieve a passage sharing none of the query's words. It is
also the only one that opens a socket, which is why offline-by-construction is a property of
*which profiles exist*, not of an operator's choice of endpoint.

### Indexing — live

**Which files are text is one object's answer.** `TextExtractorRegistry` (`SPEC-120`) holds the
extractors — plain text today — and is the one source of the extension list and of each format's
decoding. The whole-folder pass, the per-file delivery, the watcher's filter and the text search are
all handed the same registered instance; a second list would drift, and a file indexed by one path
and ignored by another looks exactly like a file that was never saved. Document formats in
containers (`.docx`, `.pdf`) get an extractor only once a rebuilt snippet is verified against its
chunk hash, and the text search asks separately whether a format scans as raw lines, so such a
format is never scanned through its packaging.

A durable **outbox indexer** in its own assembly (`src/FolderAssistant.Indexing`): a debounced
filesystem watcher, a periodic reconciler as a safety net for dropped events, and a dispatcher that
delivers one changed file at a time to the embedding pipeline with retry and backoff.

A writer inside this process reports what it changed instead of waiting to be told about it
(`IIndexChangeNotifier`), which is latency the discovery round-trip has no reason to cost.

The library composes its own loops (`FolderIndexer`, `SPEC-121`) and the application starts them
once its whole-folder pass has succeeded — the pass runs first and once, never while the front end
runs, because it embeds against a snapshot of the record and the loops move the record. The pass
records the folder through the front end's own comparison before it embeds, and writes only chunks,
vectors and the delivery mark, so `file_manifest` has one writer of what a row says. A front end that cannot start
fails the attempt, pass included, rather than leaving a ready index that has quietly stopped
following the folder. The assembly boundary is one-way on purpose: the library knows nothing of
chunking, embedding or retrieval and cannot come to depend on them by accident; the application
implements the seam (`IVectorizationService`, `RagBridgeVectorizationService`) the library delivers
through.

The change signal is deliberately coarse — the consumer re-diffs by content hash — which
makes it robust against the two ways filesystem watching is unreliable: dropped events on
buffer overflow, and atomic save-via-rename.

Properties worth stating because they are easy to "simplify" away:

- **A hold makes a multi-step edit cost one pass, and expires rather than leaking.** The quiet
  window cannot merge writes separated by the caller thinking between them, so `BeginBatch`
  lets whoever knows where its work ends say so. Holds nest and the last release publishes;
  a hold nobody releases stops suppressing after two minutes, timed from the first — the same
  rule as a reconcile loop surviving a bad pass, since an index that stops converging for the
  life of the process is the one outcome none of this may produce. Its caller is the agent-run
  boundary, which does not exist yet.
- **Whatever writes a file's record writes the columns it owns, not the row it read.** The
  library states conclusions and names operations by id; it never hands back a record it
  fetched. That rule binds the store harder than it binds the library: a pass classifying from
  a snapshot must not touch the delivery mark, and an insert that turns out to be an update
  must not clear a mark it never set — either one un-marks an indexed file and embeds it
  twice. A writer that reads a row, does something slow (an embedding round-trip), then writes
  the whole row back silently reverts whatever another writer recorded in between. The store is
  built so it cannot express that, and it is the only writer of what a row says: the repository
  writes chunks and vectors under a row's id, ends a row on a delivered removal, and has no
  statement that could create or update one — a chunk row's foreign key refuses the attempt.
  The delivery used to write the row back, and that was exactly this race.
- **A scheduled reconcile waits for the hold.** It is the one path that reaches the index
  without going through the debouncer, so a pass landing mid-hold would index a half-finished
  edit and defeat the hold entirely — bounded, because the hold expires whether or not anyone
  releases it. The kinds reported are load-bearing for the same reason: coalescing drops a
  create-then-delete pair outright, so a scratch file written and removed inside one hold costs
  nothing, and reporting that create as a modification reaches the same end state only by luck.
- **A reported change feeds the watcher's debouncer, never the per-change path directly**, and
  that routing is the whole point rather than a detail. Debouncing is what makes a dozen writes
  to one file cost one index pass; reporting past it would cost a pass per write and race the
  watcher's own events for that file into a second one — leaving write-through *worse* than
  being rediscovered. It is a no-op when nothing is running, because nothing would drain it.
- **A concurrent writer may delay indexing a file but must never stop it.** Every read
  opens share-`Read`, and on Windows a live write handle denies it (on Linux share modes are
  advisory and only the settle probe's second look sees a writer), so per-file failures are
  skipped rather than allowed to abort a pass, a change whose file is busy is retried rather
  than dropped — and the reconcile loop catches everything, because that loop *is* the safety
  net and a single fault would otherwise end it for the process lifetime.
- **A file that cannot be hashed is left out of the classification entirely**, never
  stored with an empty hash. An empty hash becomes the file's identity and makes every
  other unhashable file look like its move source.
- **Both writers of a file's record hash the same bytes with the same digest.** SHA-256 over the
  bytes, not the decoded text — decoding drops a byte-order mark — and a test runs the scanner
  against the library's hasher to hold them to it. Hashing differently would not fail; it would
  re-deliver the whole corpus after every whole-folder pass, silently.
- **A file's timestamps are the file's, not the crawl's.** Read from the filesystem when a
  file is first seen (write time where none is reported), kept across edits, and derived
  through one rule because both writers record it.
- **Skipping an unchanged file takes two conditions, not one.** The content hash must
  match *and* the active model must already have vectors for it. After switching embedding
  implementation every file is unchanged, yet none has a vector in the new model's space.
  Only embedding is skipped; chunking runs every pass, because the content has already been read
  and a corpus-fitted embedder needs the whole chunk set regardless.
- **What these loops survive is recorded, or it is invisible.** Each component takes an optional
  `ILogger` and runs silent without one — abstractions only, because a library should take a
  logger from its host rather than choose one for it — and logging never changes control flow.
  Levels follow what an operator can act on: a locked file, a hash that lost to a live writer, a
  delivery that will be retried and a checkpoint that could not be taken are **debug**; a failed
  reconcile pass, a dropped change, a file given up on after its attempt limit and a failed drain
  are **warning**; a delivery abandoned after `MaxAttempts` is **error**, and the only one,
  because that file is recorded as failed and its one other symptom is a search that quietly
  does not find it.
- Indexing runs **off** the startup path; the web host does not wait for it. A first index that
  fails is reported as failed, not as still building, and is retried on an interval.

### Retrieval — built, no runtime caller

Retrieval is **lazy** — it happens only when the model chooses to call a search tool, not
as an unconditional pipeline stage. The query and the reducer exist and are composed; the tools
that would call them do not.

- **Semantic search** embeds the query, ranks chunks by cosine similarity within a single
  embedding space, and rebuilds passage text from disk (chunks store no text). Candidates
  are over-fetched and then reduced under a token budget with hybrid semantic and lexical
  relevance, MMR diversity, and a **relative score-gap cutoff** — the calling model chooses
  how many results to ask for and does not reliably ask for few, so the cutoff drops
  candidates falling below a fraction of *this query's* best hit rather than trusting an
  absolute floor. A rebuilt snippet is to be verified against `chunk.chunk_hash` before it
  reaches the model (plan §5.2 item 1) — without that check an edit inside the settle window
  yields a wrong passage under a real path and a real score.
- **Text search** is an index-independent exact/regex folder scan, for literal lookups and
  for the window before the first index is ready.

Ahead of that reduction sits a **low-confidence screen**: when even the best candidate
falls below a floor, the whole set is refused rather than thinned. It cannot live inside the
reducer, which is contractually obliged to return its best candidate even when that one alone
busts the budget. **The floor is a property of the embedding model, so its default is off** — a
real embedder separates answerable from unanswerable questions cleanly while the placeholder's
scores overlap, and a non-zero default would claim a selectivity the default profile lacks.

While the first index builds, semantic search **refuses** rather than answering from a
half-built index: results from a partial index are indistinguishable from genuinely poor
ones. A query vector is compared only against vectors sharing its model version — scoring
across embedding spaces is meaningless.

### Tools — guard, both file holders and the search holder built, reached by nothing

The search holder (`SearchTools`) exists beside the read holder and holds `FindFilesAbout`: which files
are about a topic, each scored by its best passage. It is built over the **wrapped** `IRetrievalQuery`
the root composed, never over a reader of its own, so a file-level search is timed by the same
decorator, served by the same backend and refused by the same readiness guard as a passage search;
it over-fetches passages and folds them into files, and adds no ranking of its own. It is its own
holder because its failure contract differs: a search failure is fatal, a file failure is a string,
and the holder is what a facade tells the two apart by. `IndexNotReadyException` passes through it.

The read holder (`ReadTools`) exists: `InspectDirectory`, `ReadFile`, `Retrieve`, `FindFiles`,
`SearchText`, each over the holder's own guard. Every bound is a code constant, every cut is said in
the result's note with a `Truncated` flag, and every hard failure is the ordinary exception — the
facade that turns one into the string the model must report is Phase B. `FindFiles` and `SearchText`
share one walk that builds its own list, skipping the metadata folder, the scanner's ignored directory
names and every link, so neither the matcher nor the search touches the live tree. A pattern with no
separator matches the file name at any depth; that is the form a model reaches for first.

The mutation holder (`MutationTools`) exists: `Create`, `Update`, `ReplaceLines`, `Delete`, over a
second guard of its own. `Update` is a literal replace in one pass whose replacer keeps the scan
cursor and the copy cursor apart — a candidate the whole-word rule rejects advances the first and
not the second, or the text between would be dropped silently with the count still right — and
throws when the text does not occur, because a change that changed nothing is the silent failure
this tree exists to refuse. `ReplaceLines` locates an inclusive range through `LineRangeLocator`,
tested on its own for every line-ending form. A rewrite keeps the byte-order mark and the line
ending it found. Every completed mutation is reported to the running front end through
`IIndexChangeNotifier` with its own kind, one report per file, and the report is advisory: one that
fails is a note on the result, never a failure of a write that happened. Both holders read a file
through one `TextFile` helper, so they cannot mean different things by "a text file".

`SearchText` reads exactly the files the index does — the extension list asked of the text
extraction registry, narrowed to the formats that scan as raw lines, and the scanner's size bound
passed in from the same configuration value — so a search and the index cannot disagree about what
the folder contains. It is bounded four ways and says which one applied: matching lines, matched
characters, a whole-search deadline that returns a partial result with a note, and the caller's
token, which throws. `WholeWord` goes through one shared `WordBoundary` rule for the literal and the
regex form alike, so no two tools can mean different things by it.

File tools are split into two holders — **read** (`InspectDirectory`, `ReadFile`, `Retrieve`,
`FindFiles`, `SearchText`) and **mutation** (`Create`, `Update`, `ReplaceLines`, `Delete`) — with the
**search** holder (`FindFilesAbout`) beside them under the fatal contract. The split is
the contract, not file layout: it is what lets a roster grant an agent the ability to read
the folder without the ability to change it, so "which agent can destroy data" is answerable
by reading one line of configuration. There is no shell-execution tool.

Every caller-supplied path resolves through a containment guard (`WorkspacePathGuard`, `SPEC-101`
— built, held by nothing yet) that refuses anything outside the workspace root. The textual rule
decides on the normalised full path, and then each way it can be fooled is closed: a symbolic link
or junction at **any** segment below the root, a file whose NTFS hard-link names include one outside
the root, and a `subst` drive letter standing for a path that is. Only a *redirecting* reparse point
is refused — a cloud-file placeholder redirects nothing, and refusing it would make a synced folder
unusable while closing no escape. The `subst` check resolves one level; a chain or an unrecognised
device target leaves the textual rule deciding and puts a note on the result, which a tool passes
on. The metadata folder is refused for **every** operation, reads included, and hidden from
listings. Each holder gets its own guard instance over the same root.

A containment test whose fixture cannot be staged — a junction not created, a `subst` letter not
claimed — throws and fails; the `subst` cases exist only on Windows and are reported as skipped
elsewhere, never as passed.

Mutations write through a temp file and a **retrying rename**, never an in-place write: an
in-place write holds a handle the indexer's share-`Read` opens collide with, and a blocked
replace is reported as either `IOException` or `UnauthorizedAccessException` depending on
the platform, so both must be retried. Deletion needs the same retry — share-`Read` carries
no delete permission. Reads deliberately do *not* retry: two readers coexist, so a failing
read means a genuine external writer and should say so promptly.

**Search tool failures are fatal; file tool failures are strings.** A swallowed retrieval fault
is indistinguishable from "nothing relevant", and the model would answer from prior knowledge
believing it had searched. A file tool failure returns a `TOOL_FAILED:` string the model must
report rather than answer around.

### Agent — provider client, agent factory and tool facades built, reached by nothing

One provider family in two shapes, OpenAI-compatible and Azure, built by `ProviderClientFactory`
(`SPEC-140`). A client is **built, not connected**: nothing reaches the network at construction, so
the host boots with a provider it cannot reach and the first turn fails instead of startup. What is
refused at construction is a configuration that names no provider at all — the hosted API without
a key, Azure without an endpoint or a key — with a sentence naming the key and the environment
variable, because a client that would fail every call with a transport error is a plausible failure
a page later. The SDK's own network timeout defaults to 100 seconds whatever the host configures, so
both option shapes set it to the configured connection timeout. This factory is the one place
document text can leave the machine, and `SPEC-000` names it as the deliberate exception to the
offline rule.

`AgentFactory` makes the framework's chat-client agent from the configuration: the configured
system prompt **verbatim**, or one built from the name and description; the tool list as what it may
call, none meaning none; and `AgentHandle` owning agent and client together, because the framework's
agent does not own its client and a client is a pipeline something has to end. The root registers
both as factories, resolved by nothing.

The tools the agent holds are the holders' own methods, reflected (`ToolReflection`): a public method
carrying a `[Description]` is a tool named after it, with the holder's parameter descriptions as its
schema, and nothing is described twice. Each is wrapped in **one facade** (`ToolFacade`) that times the
call, writes one structured log line and applies the failure contract of its **group**, and the group
is decided by the holder's type in one place (`ToolSet`), so a call site cannot put the search holder
under the file contract. A file tool that throws returns `TOOL_FAILED: <tool>: <message>`, logged at
warning, and the built prompt tells the model what that prefix means. A search tool that throws — the
index's readiness refusal included — throws through unchanged, logged at error, and **the turn ends**:
the agent's tool-calling loop is built by `AgentFactory` with no tolerance for a failed iteration,
because the framework's default hands the model a generic error string and lets it answer, which is
exactly the swallowed fault. A cancellation of the caller's token is classified by the token before the
exception type and passes through both groups. Two tools with one name are refused at construction.

### Persistence — index database live, conversation database not built

Two SQLite databases in the folder's metadata directory (`.folderassistant/`), and the split is
deliberate:

- **the index** (`manifest.db`) — files, chunks, vectors, the outbox, the embedding model
  registry, the fit artifact;
- **conversation state** — history and session blobs. Not built.

An index rebuild drops and repopulates the index schema, and conversation history has to
survive that. They also have opposite access patterns: the index has one writer and many
readers, while conversation state is written on the request path once per turn.

Rules that bite:

- **All connections go through one connection factory.** Never hand-roll a connection
  string. `PRAGMA foreign_keys` and `busy_timeout` are per-connection and are not
  persisted, so every connection must set them for itself — cascading deletes silently do
  nothing without the first.
- **No shared cache.** On a file-backed database it makes connections share one cache,
  which faults when a handle is finalized while another connection prepares a statement,
  and it converts contention into a lock error for which the busy handler is never invoked,
  quietly defeating `busy_timeout`.
- **No connection pooling either**, since 2026-09-16 (`SPEC-130` 0.14.0). The pool handed one
  `sqlite3` handle to two threads about once in 1,500 concurrent opens — measured with no pool
  clearing anywhere in the process, so a running application was exposed, not only the suite — and
  showed as `SQLITE_ERROR` (code 1) out of `BeginTransaction`, never `SQLITE_BUSY`. The price,
  measured both ways on one tree: an open costs about 0.35 ms against 0.01 ms pooled, and a fresh
  connection has a cold page cache — about 10 ms on a retrieval over 24,000 vectors, a tenth more
  on a cold index. A retry around `BEGIN` was rejected because it hides one presentation of a
  shared handle and leaves the rest. If
  that error signature ever returns, something is opening a connection outside the factory. **Do not
  lower the concurrent-bootstrap test's concurrency**; eight is what makes the property testable.
- **No store runs DDL.** Schema creation lives in one bootstrapper per database, run once
  before the server accepts a request. DDL from the request path costs a round-trip per
  turn and forces read paths onto write-capable connections.
- **WAL** is set once at creation. It is the only reason retrieval can read while the
  background indexer writes. Its log is reclaimed by a truncating checkpoint at exactly two
  quiet moments — a whole-folder pass finishing, and the outbox drain going quiet after
  delivering work — and never per write or on an idle poll. Advisory: one that cannot be taken
  costs disk, never correctness.
- Vectors are keyed `(chunk_id, model_version_id)` so multiple embedding models can coexist
  during a migration; exactly one model is active for write. Switching profiles changes the
  active model version; existing vectors are not invalidated, they stop being the active ones.

### Observability — live

Telemetry sits in **decorators** over the composed seams — one for embedding, one for
retrieval — so every call is recorded regardless of which backend a profile selected.
Compose through the provided `Wrap` helpers rather than the plain constructors: the
wrappers re-expose the inner implementation's optional interfaces, and one that dropped
them would disable fitting and health checks while still returning good-looking vectors.

Two classifications are load-bearing. **Distinguish "still building" from "the build
failed"** — the first is the expected condition every process hits before its first index
and must not drag down an error rate; the second is a real fault. And **classify a fault by
the caller's cancellation token before its exception type**: an HTTP client reports its own
deadline as a cancellation, so type-first filing would record a dead endpoint as a user
closing a tab.

The retrieval sink emits through **two channels**: a structured log line, and a `Meter`
exported at `GET /metrics` for a Prometheus scrape. The log line is what someone reads about
one odd query; the meter is what a dashboard reads, and it keeps working when the log level
is raised to suppress routine successes. Operator reference:
[docs/observability-retrieval.md](docs/observability-retrieval.md).

## Code conventions

Two conventions, one per assembly, on purpose — `.editorconfig` enforces the split by path.
**Match the file you are editing.**

The application project (`FolderAssistant/`) and the tests:

- **Indentation is tabs.**
- **BCL type names are spelled out**: `String`, `Int32`, `Boolean`, `Single`, `Double` —
  not the C# keywords. Set to *warning* because it is the rule pasted sample code breaks.
- The `this.` qualifier is used for instance member access; private fields are `_camelCase`.
- `var` only where the type is already on the line; braces on every block, even one statement.
- Domain types are `internal sealed record` / `internal sealed class`; nullable reference
  types and implicit usings are enabled.

The indexing library (`src/FolderAssistant.Indexing`) is **standard modern C#**: `string`, `var`,
file-scoped namespaces.

Tests see both assemblies' internals via `InternalsVisibleTo`; the application also grants
`DynamicProxyGenAssembly2`, because Moq builds proxies in a dynamic assembly and mocking an
internal interface needs it. Keep production types `internal` rather than `private` when they
need coverage.

**Comments claim only what the tree can show.** No forward references, no invented rationale, no
summarising the code — comment the constraint, the measurement, the rejected alternative. A comment
asserting a mechanism the tree does not have is worse than no comment.

## Testing

xUnit with FluentAssertions and Moq, one test project for both assemblies. Smoke tests boot the
real host through `WebApplicationFactory<Program>`, so they also trigger database bootstrap and
indexing; library tests live under `FolderAssistant.Tests/Indexing/`.

Habits that matter more than a coverage number here:

- **Assert on the thing directly, not only end to end.** Offset arithmetic and text
  extraction have failure modes an end-to-end retrieval check cannot see, because ranking
  still succeeds on mangled text.
- **If a test goes red, capture the output before re-running.** A re-run that passes
  discards the only diagnostic there is — and a test that fails a third of the time and is
  *correct* looks exactly like one that is flaky.
- **Mutation-test the property a change claims.** Diff the file after patching — an unchanged
  file is a broken mutation, not a strong test — and a mutation that only fails to *compile*
  under the zero-warning gate is a weaker kill than a failing test.
- **Verify a commit standing alone**, in a throwaway `git worktree` at that commit, before
  believing a stability claim. It has caught defects the tip-only build hid.
- **A new test whose fixture cannot be staged fails; it never skips green.** The two existing
  early-return suites (Ollama live, `sqlite-vec` backend) are a documented exception, not a
  convention to extend — containment tests here once asserted nothing for weeks.

## Documentation

- [`docs/specs/`](docs/specs/) — one versioned spec per module; the requirement a change is
  written from. Start at `SPEC-000`; the storage choice is `SPEC-131`. `SPEC-000` is known to
  overclaim on two points (no network reach; an agent that exists) until its rewrite lands.
- [`docs/diagrams/`](docs/diagrams/) — the architecture as it is meant to hold together, and
  the live / built / not-built status.
- [`docs/benchmarks/`](docs/benchmarks/) — figures of the tree that measured them, never of the
  current one.
- [`docs/archive/`](docs/archive/) — designs considered and not built, kept for the reasoning
  that rejected them.

`CLAUDE.md` and the specs carry **invariants and checked negatives** — things that change what the
next person does. Session narrative goes in `notes/`, dated, and is pruned when it stops changing
anything.

## Repository hygiene

- The metadata folder (`.folderassistant/` by default) is generated output: gitignored,
  never edited or committed by hand. Deleting it and running again rebuilds it.
- `notes/`, `*.local.md`, `CLAUDE.local.md`, `.mcp.json` and `appsettings.Development.json`
  are gitignored and stay local. The last of these may hold a real API key; create it from
  `appsettings.Development.template.json`.
- Never commit credentials. Configuration binds from environment variables
  (`FolderAssistant__Provider__ApiKey`) and user secrets as readily as from `appsettings.json`.
