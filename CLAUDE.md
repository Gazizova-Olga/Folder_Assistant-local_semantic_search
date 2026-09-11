# CLAUDE.md

Guidance for Claude Code working in this repository.

## What this is

**Folder Assistant** — a local-first LLM agent that indexes a folder on disk, exposes
a confined set of filesystem tools over it, and answers questions about its contents
using retrieval-augmented generation. A single ASP.NET Core application built on the
Microsoft Agent Framework (`Microsoft.Agents.AI.*`) and `Microsoft.Extensions.AI`.

The design goal is that the whole loop can run **offline**: embeddings from a local
Ollama model, vectors in a folder-scoped SQLite database, retrieval by cosine similarity
or native `sqlite-vec` k-NN. No document text leaves the machine unless the operator
points the agent at a hosted chat provider.

## Status: rebuild in progress

This repository is being built up deliberately, one step at a time. Code lands here only once
it has been written for this repository rather than carried in, so at any given commit most
of the architecture below is intent rather than description.

Two rules follow from that, and they are load-bearing:

- **`_staging/` is reference material, not source.** It is gitignored. Nothing in it is
  ever `git add`ed, committed, or published — not even temporarily, because a commit that
  contains it keeps containing it after the file is deleted in a later commit.
- **Nothing is pushed until the rebuild is complete.** The remote exists and is private;
  it stays empty until the tracked tree stands on its own.

Working notes, sequencing and the current state of the rebuild live in
`notes/transfer-plan.md` (gitignored, local only). Read it before deciding what to build
next or what to leave out — it is the authority on scope, not this file.

## Commands

From the repository root:

```bash
dotnet build                                  # build the solution
dotnet test                                   # run all tests (xUnit)
dotnet run --project FolderAssistant          # agent + web host
```

Filtered test runs:

```bash
dotnet test --filter "FullyQualifiedName~FolderIndexingPipelineTests"
dotnet test --filter "DisplayName~Cascades"
```

Targets **.NET 10**. NuGet versions are managed centrally in `Directory.Packages.props`
(Central Package Management) — **do not put versions in `.csproj` files**.

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

## Target architecture

Written down so the rebuild has something to converge on. These sections describe intent;
they are not a description of code that exists until the code exists.

### Composition root

`Program.cs` is a composition root, not a script: it registers factories and lets the host
resolve them in order. Configuration binds **lazily through `IOptions<T>`**, never eagerly
off `builder.Configuration` — an eager bind freezes values before the host is built and
silently discards any configuration source added afterwards, including the one a test host
injects. Only the listen port and the registered agent name may be read early, because
they cannot be late-bound.

A composition profile resolved from configuration bundles the vectorizer, the vector store
reader/writer and the retrieval strategy **together**, so a mismatched combination — a
lexical query against a neural-embedded store, say — cannot be expressed. An unknown or
platform-unavailable profile is a startup failure, never a silent fallback. One vectorizer
instance is shared by indexing and retrieval: a corpus-fitted embedder must be fitted
identically on the write and the query side.

Three embedders exist behind that seam. The **programmable** one is a character histogram — a
deterministic floor with no semantics, and the offline default. The **LSA** one is fitted to the
corpus and captures weak synonymy within it. The **Ollama** one (`SPEC-162`) is a real pretrained
model served locally, and is the only one that can retrieve a passage sharing none of the query's
words. It is also the only one that opens a socket, which is why offline-by-construction is a
property of *which profiles exist*, not of an operator's choice of endpoint.

### Indexing

A durable **outbox indexer** in its own assembly (`src/FolderAssistant.Indexing`): a debounced
filesystem watcher, a periodic reconciler as a safety net for dropped events, and a dispatcher that
delivers one changed file at a time to the embedding pipeline with retry and backoff.

**The watcher, the reconciler and the per-change pipeline exist so far** (`SPEC-121`) — the outbox
and dispatcher are not built, and the store both writers apply their conclusions to has no
implementation yet. The assembly boundary is one-way on purpose:
the library knows nothing of chunking, embedding or retrieval and cannot come to depend on them by
accident.

The change signal is deliberately coarse — the consumer re-diffs by content hash — which
makes it robust against the two ways filesystem watching is unreliable: dropped events on
buffer overflow, and atomic save-via-rename.

Properties worth stating because they are easy to "simplify" away:

- **A concurrent writer may delay indexing a file but must never stop it.** Every read
  opens share-`Read`, and a live write handle denies it, so per-file failures are skipped
  rather than allowed to abort a pass, a change whose file is busy is retried rather than
  dropped — and the reconcile loop catches everything, because
  that loop *is* the safety net and a single fault would otherwise end it for the process
  lifetime.
- **A file that cannot be hashed is left out of the classification entirely**, never
  stored with an empty hash. An empty hash becomes the file's identity and makes every
  other unhashable file look like its move source.
- **A file's timestamps are the file's, not the crawl's.** Recording the moment a scan ran
  as a file's creation date is not metadata about the file.
- **Write the columns you own, not the row you read.** A writer that reads a row, does
  something slow (an embedding round-trip), then writes the whole row back will silently
  revert whatever another writer recorded in between.
- **Skipping an unchanged file takes two conditions, not one.** The content hash must
  match *and* the active model must already have vectors for it. After switching embedding
  implementation every file is unchanged, yet none has a vector in the new model's space —
  so skipping on the hash alone leaves the new model with a silently empty index. Only
  embedding is skipped; chunking runs every pass, because the content has already been read
  and a corpus-fitted embedder needs the whole chunk set regardless.
- Indexing runs **off** the startup path; the web host does not wait for it.

### Retrieval

Retrieval is **lazy** — it happens only when the model chooses to call a search tool, not
as an unconditional pipeline stage. Two tools, deliberately separate:

- **Semantic search** embeds the query, ranks chunks by cosine similarity within a single
  embedding space, and rebuilds passage text from disk (chunks store no text). Candidates
  are over-fetched and then reduced under a token budget with hybrid semantic and lexical
  relevance, MMR diversity, and a **relative score-gap cutoff** — the calling model chooses
  how many results to ask for and does not reliably ask for few, so the cutoff drops
  candidates falling below a fraction of *this query's* best hit rather than trusting an
  absolute floor.
- **Text search** is an index-independent exact/regex folder scan, for literal lookups and
  for the window before the first index is ready.

Ahead of that reduction sits a **low-confidence screen**: when even the best candidate
falls below a floor, the whole set is refused rather than thinned, because a handful of
weak passages costs context tokens and invites an answer built on text that does not
address the question. It cannot live inside the reducer, which is contractually obliged to
return its best candidate even when that one alone busts the budget. **The floor is a
property of the embedding model, so its default is off** — measured, a real embedder
separates answerable from unanswerable questions cleanly while the placeholder's scores
overlap, and a non-zero default would claim a selectivity the default profile lacks.

While the first index builds, semantic search **refuses** rather than answering from a
half-built index: results from a partial index are indistinguishable from genuinely poor
ones. A query vector is compared only against vectors sharing its model version — scoring
across embedding spaces is meaningless.

### Tools

File tools are split into two holders — **read** (`InspectDirectory`, `ReadFile`,
`FindFiles`) and **mutation** (`Create`, `Update`, `ReplaceLines`, `Delete`). The split is
the contract, not file layout: it is what lets a roster grant an agent the ability to read
the folder without the ability to change it, so "which agent can destroy data" is answerable
by reading one line of configuration. There is no shell-execution tool.

Every caller-supplied path resolves through a containment guard that refuses anything
outside the workspace root, including a reparse point at any path segment. The metadata
folder is refused for **every** operation, reads included, and hidden from listings.

Mutations write through a temp file and a **retrying rename**, never an in-place write: an
in-place write holds a handle the indexer's share-`Read` opens collide with, and a blocked
replace is reported as either `IOException` or `UnauthorizedAccessException` depending on
the platform, so both must be retried. Deletion needs the same retry — share-`Read` carries
no delete permission. Reads deliberately do *not* retry: two readers coexist, so a failing
read means a genuine external writer and should say so promptly rather than after the full
retry budget.

### Persistence

Two SQLite databases in the folder's metadata directory, and the split is deliberate:

- **the index** — files, chunks, vectors, the outbox, the embedding model registry;
- **conversation state** — history and session blobs.

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
- **No store runs DDL.** Schema creation lives in one bootstrapper per database, run once
  before the server accepts a request. DDL from the request path costs a round-trip per
  turn and forces read paths onto write-capable connections.
- **WAL** is set once at creation. It is the only reason retrieval can read while the
  background indexer writes.
- Vectors are keyed `(chunk_id, model_version_id)` so multiple embedding models can coexist
  during a migration; exactly one model is active for write.

### Observability

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

Non-obvious and applied consistently — match them:

- **Indentation is tabs** in the application project.
- **BCL type names are spelled out**: `String`, `Int32`, `Boolean`, `Single`, `Double` —
  not the C# keywords.
- The `this.` qualifier is used for instance member access.
- Domain types are `internal sealed record` / `internal sealed class`; nullable reference
  types and implicit usings are enabled.
- **The indexing library, once it exists, is standard modern C#** (`string`, `var`,
  file-scoped namespaces). **Match the file you are editing**; the two conventions coexist on
  purpose, one per assembly.
- Tests see application internals via `InternalsVisibleTo`. Keep production types
  `internal` rather than `private` when they need coverage.

## Testing

xUnit with FluentAssertions and Moq. Smoke tests boot the real host, so they also trigger
database bootstrap and indexing.

Two habits matter more than a coverage number here:

- **Assert on the thing directly, not only end to end.** Offset arithmetic and text
  extraction have failure modes an end-to-end retrieval check cannot see, because ranking
  still succeeds on mangled text.
- **If a test goes red, capture the output before re-running.** A re-run that passes
  discards the only diagnostic there is — and a test that fails a third of the time and is
  *correct* looks exactly like one that is flaky.

## Repository hygiene

- The metadata folder (`.folderassistant/` by default) is generated output: gitignored,
  never edited or committed by hand.
- `notes/`, `*.local.md`, `_staging/`, `CLAUDE.local.md` and `appsettings.Development.json`
  are gitignored and stay local. The last of these may hold a real API key.
- Never commit credentials. Configuration binds from environment variables and user secrets
  as readily as from `appsettings.json`.
