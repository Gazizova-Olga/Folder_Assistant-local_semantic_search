# Folder Assistant

A local-first assistant for one folder on your disk. Point it at a directory and it builds a
semantic index of the text files inside, keeps that index in step with the folder as files change,
and — once the agent layer lands — answers questions about the folder's contents through an LLM that
retrieves only the passages it needs.

Everything runs in **one process, over one folder, with no service to deploy**. Embeddings come from
a local model, vectors live in a SQLite database inside the folder, and no document text leaves the
machine unless you deliberately point the chat side at a hosted provider.

| | |
|---|---|
| Language / runtime | C# 14 on .NET 10, ASP.NET Core minimal host |
| AI stack | `Microsoft.Extensions.AI`, built towards the Microsoft Agent Framework; Ollama for local embeddings |
| Storage | SQLite via `Microsoft.Data.Sqlite`, WAL mode; `sqlite-vec` native k-NN by default, brute-force cosine where it has no binary |
| Observability | OpenTelemetry metrics exported for Prometheus at `GET /metrics`; structured logging |
| Quality gates | Zero-warning build enforced with SonarAnalyzer; ~750 xUnit tests; CI on Ubuntu and Windows for every push |
| Method | Spec-first: every behavioural change starts from a versioned spec in `docs/specs/` and ships with it |

## Why it exists

Search over your own files should not require uploading them anywhere, and an assistant that reads
your files should never invent an answer and present it as if it came from them. Both aims shape the
design:

- **Offline by construction.** The embedding profiles that ship are in-process or loopback-only
  (Ollama on `localhost`). Which profiles exist is what keeps the system offline, not an operator's
  choice of endpoint.
- **Fail loudly, never plausibly.** The failure mode the whole design guards against is a silently
  plausible wrong answer. A search while the first index is still building is refused rather than
  answered from a half-built index; an unknown embedding profile stops the application at startup
  instead of falling back; a scan that finds nothing throws instead of quietly indexing nothing.
- **Reading is not writing.** The file tools are split into a read holder and a mutation holder,
  each over its own containment guard, and the roster grants tools by name, so which agent can change
  data is answerable from one line of configuration.

The full statement of purpose and principles is [SPEC-000](docs/specs/SPEC-000-system-concept.md).

## Status

The project is under active development. The tree is in three states, and
[docs/diagrams/implementation-status.md](docs/diagrams/implementation-status.md) shows every block
colour-coded.

**Live and running**

- The **whole-folder indexing pass** at startup: scan, chunk, embed, store.
- The **file-indexing front end** afterwards: a debounced filesystem watcher, a periodic reconciler
  that catches dropped events, and a durable outbox that delivers one changed file at a time to the
  embedding pipeline with retry and backoff.
- The **index database** — files, chunks, vectors and the outbox in a folder-scoped SQLite file.
- The **text extraction registry** — the one source of which files are read and how each is
  decoded, asked by the indexing pass, the per-file delivery, the watcher and the text search alike.
  Plain text today; document formats wait until a rebuilt passage is verified against its chunk
  hash.
- **Telemetry** — a structured log line per embed call, and retrieval metrics at `GET /metrics`.

**Built and tested, not yet reachable at runtime**

- Retrieval — cosine ranking within one embedding space, in managed code or through `sqlite-vec` —
  and the context reducer that reranks, diversifies and fits passages under a token budget. Nothing
  on the request path calls them yet, so the vectors are written and read only by the test suite.
- The **workspace path guard** every file tool resolves paths through: it refuses anything outside
  the folder, including escapes through symbolic links and junctions, hard links, and `subst` drives,
  and refuses the index's own metadata folder for every operation.
- The **read tools** over that guard: list a directory, read a numbered line range, read a bounded
  whole file, find files by glob. Every cut is said in the result; every hard failure is an exception.
- The **text search** beside them: a literal or regular-expression scan of the folder's text files,
  line by line, returning each matching line with its file and line number. It reads the files, not
  the index, so it answers exact lookups and works before the first index is ready. Bounded by
  matching lines, matched characters, a deadline and the caller's cancellation, and every cut is said.
- The **file-level semantic search**, in its own holder: which files are about a topic, by meaning,
  each with its best passage's score. It asks the same wrapped retrieval query the passage search
  will, so it is timed by the same instrument and refused by the same readiness check, and adds no
  ranking of its own.
- The **mutation tools**, in their own holder over a second guard: create a file, replace text in
  one, replace a line range, delete a file or a directory. Every write is a temporary file and a
  rename, retried while the indexer holds the file open; every completed change is reported to the
  index with its own kind, so the index follows the tool's edits without rediscovering them.
- The **provider client and the agent**: one client family in two shapes, OpenAI-compatible (a local
  Ollama chat model included, with no key) and Azure OpenAI, built but never connected at startup,
  with the SDK's network timeout set to the configured one; and one agent over it, with the
  configured prompt verbatim.
- The **tool reflection and the facades**: every method the three holders describe is a tool named
  after it, each wrapped in a facade that times it, logs it and applies its group's failure contract.
  A file tool's failure comes back to the model as a `TOOL_FAILED:` string it is told to report; a
  search tool's failure ends the turn, because a swallowed retrieval fault reads exactly like "nothing
  relevant".
- The **roster**: which agents exist, what each may call and delegate to, and which one every turn
  enters. One agent holding every tool by default; a default roster in code — an orchestrator
  delegating to a reader and a mutator — behind a switch until its cost is measured; or the operator's
  own. Validated whole at startup, so a delegation cycle or an unknown tool stops the host before it
  listens. Each agent is built over its own client with one delegation tool per target.
- The **turn runner**: one turn of one conversation through the roster's coordinator — the agent's
  session loaded, the agent run, the session saved when the turn succeeded — behind the chat-client
  interface a front end will talk to. Sessions are held in memory until the conversation database
  exists. Each turn is timed and classified inside the execution, because a streamed turn that fails
  says so as text and ends cleanly, which anything watching from outside would count as a success.
  No front end exists, so nothing calls it yet.

**Not built**

- The semantic passage search tool, the chat surface (console, HTTP), the conversation database.

In practice: you can run the application today to **index a folder, watch it follow your edits and
read the telemetry**. You cannot yet ask it a question.

### The whole structure, coloured by state

Every block the design converges on. Green runs; orange is built and tested but reached by nothing at
runtime; grey is planned; amber runs with a known defect queued against it; dashed is deliberately
deferred. Solid arrows are calls that exist, dotted ones seams with only one side built. The source is
[docs/diagrams/implementation-status.md](docs/diagrams/implementation-status.md), which also carries the
per-item ledger; a test keeps this copy identical to it.

```mermaid
flowchart TB
    subgraph composition["Composition — Program.cs"]
        direction LR
        root["Composition root<br/>lazy IOptions, startup filter"]
        profiles["CompositionProfiles<br/>6 named bundles; ollama-vec default, loud store fallback to ollama-blob"]
        config["AgentConfig<br/>section FolderAssistant"]
        metrics["GET /metrics<br/>Prometheus exporter"]
    end

    subgraph embedding["Embedding — SPEC-160/161/162"]
        direction LR
        prog["ProgrammableEmbeddingVectorizer<br/>character histogram"]
        lsa["LsaEmbeddingVectorizer<br/>corpus-fitted"]
        ollama["OllamaEmbeddingVectorizer<br/>qwen3-embedding, local, bounded calls"]
        etel["EmbeddingTelemetryVectorizer<br/>Wrap, probe re-exposed"]
    end

    subgraph indexing["Indexing — SPEC-120/121"]
        direction LR
        pass["Whole-folder pass<br/>record, fit, embed, mark delivered"]
        state["IndexState<br/>Building · Ready · Failed, retried"]
        indexer["FolderIndexer<br/>watcher · reconciler · change pipeline · dispatcher"]
        store["FolderIndexStore<br/>one writer of file_manifest + outbox"]
        bridge["RagBridgeVectorizationService<br/>chunks + vectors under the delivered id; unfitted fails"]
        hold["BeginBatch hold<br/>nests, expires"]
    end

    subgraph persistence["Persistence — SPEC-130"]
        direction LR
        boot["FolderDatabaseBootstrapper<br/>manifest.db, WAL, migrations"]
        conn["FolderDatabaseConnection<br/>foreign_keys, busy_timeout, no shared cache, no pool"]
        blob["Blob vector store<br/>chunk_vector"]
        vec["sqlite-vec store<br/>vec0 per model"]
        convdb["conversations.db<br/>bootstrapper, ConversationStore"]
    end

    subgraph retrieval["Retrieval — SPEC-110"]
        direction LR
        cosine["CosineRetrievalQuery"]
        vecq["SqliteVecRetrievalQuery"]
        floor["RelevanceFloor<br/>low-confidence screen"]
        reducer["TokenBudgetContextReducer<br/>hybrid rerank, MMR, score gap"]
        rtel["RetrievalTelemetryQuery<br/>log line + meter"]
        snippet["Snippet verification<br/>against chunk_hash"]
    end

    subgraph tools["Tools — Phase A, SPEC-101"]
        direction LR
        guard["WorkspacePathGuard<br/>containment, links, hard links, subst, metadata folder"]
        readt["Read tools<br/>InspectDirectory · ReadFile · Retrieve · FindFiles"]
        textsearch["SearchText<br/>index-independent, bounded four ways"]
        about["FindFilesAbout<br/>file-level semantic, same seams"]
        mutate["Mutation tools<br/>Create · Update · ReplaceLines · Delete"]
        extract["Text extraction registry<br/>one source of the extension list; plain text; docx/pdf after snippet verification"]
    end

    subgraph agent["Agent and orchestration — Phase B, SPEC-100/140"]
        direction LR
        provider["Provider + agent factory<br/>OpenAI-compatible, Azure; NetworkTimeout set"]
        facade["Tool facades<br/>file tools non-fatal · search tools fatal"]
        roster["Roster · catalog · registry · routing<br/>default roster in code; cycles refused at startup"]
        runner["WorkflowRunner / IAgentExecution<br/>turn telemetry inside the execution; sessions in memory"]
        searchidx["SearchIndex tool<br/>embed, over-fetch, screen, reduce, memoize per turn"]
        batching["Index batching across a turn<br/>BeginBatch at the agent-run boundary"]
        console["Console loop<br/>beside the host; stdin EOF does not stop it"]
    end

    subgraph surface["Conversation and HTTP — Phase C, SPEC-170 to write"]
        direction LR
        turns["TurnRecordingAgent<br/>message capture over the roster"]
        history["GET/DELETE /api/history"]
        status["GET /api/index/status<br/>read-only, bounded failed sample"]
        responses["OpenAI Responses endpoints + DevUI<br/>SQLite conversation storage, loopback only"]
        provenance["Provenance framing<br/>file content is data, not instructions"]
    end

    subgraph deferred["Deferred, deliberately"]
        direction LR
        webui["Custom web UI"]
        approvals["Human-in-the-loop approvals<br/>IApprovalGate"]
        legacydoc[".doc extraction"]
        hybrid["Hybrid lexical recall<br/>FTS5 union"]
    end

    root --> profiles --> etel
    etel -.-> prog & lsa & ollama
    root --> pass --> indexer
    indexer --> store --> bridge
    pass -.-> state
    bridge --> blob & vec
    blob & vec -.->|"vectors nothing reads"| cosine & vecq
    cosine & vecq --> floor --> reducer --> rtel -.-> metrics
    rtel -.-> searchidx
    snippet -.-> searchidx
    guard --> readt & textsearch & mutate & about
    readt & textsearch & about & searchidx & mutate --> facade --> roster --> runner
    provider --> roster
    runner --> batching -.-> hold
    runner --> console & turns & responses
    turns --> convdb
    history & status --> convdb
    provenance -.-> facade

    classDef live fill:#1b5e20,stroke:#a5d6a7,color:#ffffff
    classDef defect fill:#8d6e00,stroke:#ffe082,color:#ffffff
    classDef built fill:#e65100,stroke:#ffcc80,color:#ffffff
    classDef planned fill:#37474f,stroke:#b0bec5,color:#ffffff
    classDef deferredCls fill:#263238,stroke:#546e7a,color:#b0bec5,stroke-dasharray:4 3

    class root,config,metrics,profiles,prog,lsa,ollama,etel,pass,state,indexer,store,bridge,hold,boot,conn,blob,vec,rtel,extract live
    class cosine,vecq,floor,reducer,guard,readt,textsearch,about,mutate,provider,facade,roster,runner built
    class convdb,snippet,searchidx,batching,console,turns,history,status,responses,provenance planned
    class webui,approvals,legacydoc,hybrid deferredCls
```


## Quick start

### Requirements

- **.NET 10 SDK** — `dotnet --version` prints `10.x`.
- **Ollama**, which the default profile embeds through. Install it, keep it running, and pull the
  embedding model — or name an in-process profile (see *Choosing a profile*) to run with nothing
  installed:

  ```bash
  ollama pull qwen3-embedding:0.6b
  ```

### Build

```bash
git clone https://github.com/Gazizova-Olga/Folder_Assistant-local_semantic_search.git
cd Folder_Assistant-local_semantic_search
dotnet build --no-incremental -v q --nologo
```

Expect **succeeded, 0 warnings**. Warnings are errors in this repository, so anything else is a
failed build.

### Run

```bash
dotnet run --project FolderAssistant
```

With no configuration the application indexes **the directory it was started from**, so start it
somewhere you mean. It listens on `http://localhost:5000`:

| Endpoint | Returns |
|---|---|
| `GET /` | The application name, the absolute path of the folder being indexed, the active profile, and a note if the default fell back to its blob twin. |
| `GET /metrics` | Prometheus text: retrieval counters and latency histograms, per backend; and turn counters and latency, per agent and status, once a turn has run. |

**This writes to the folder it is pointed at.** It creates a `.folderassistant/` directory holding
`manifest.db`, indexes every eligible text file, then keeps the index following the folder — watching
for changes, reconciling every five minutes as a safety net, re-embedding each changed file — until
you stop it. The database is derived state: delete `.folderassistant/` and run again to rebuild it.

Indexing runs off the startup path, so the host is up before the first pass finishes. The log says
when the pass is done and the watcher has taken over:

```
Reconciled C:\some\folder at start: 312 files examined, 0 skipped, 312 changes recorded.
```

Each embed call logs one structured line (`embedding provider=… model=… dim=… status=…`), which is
how you see the indexer at work after that. A first index that fails is reported as failed, not as
still building, and is retried every 30 seconds. With the default profile and no Ollama running,
that is what you will see first: `Initial index failed: The Ollama embedding backend is not usable`,
with the endpoint and model named, repeated on each retry until the server is up and the model
pulled. Nothing else embeds in its place; name `lsa-vec` if you want an index without the server.

**What gets indexed.** Plain-text files by extension — `.txt`, `.md`, `.json`, `.yml`, `.xml`,
`.csv`, `.log`, and the common source-code extensions (`.cs`, `.py`, `.js`, `.ts`, `.java`, `.go`,
`.rs`, `.sql`, `.html`, `.css`, shell and PowerShell scripts, project files) — up to 1 MB each. The
directories `.git`, `.vs`, `bin`, `obj`, `node_modules` and the metadata folder itself are skipped.
`.docx` and `.pdf` are deliberately not yet supported.

### Try it

1. Make a scratch folder with a few `.md` or `.txt` files in it.
2. Start the application from that folder and watch the log for the `Reconciled … at start` line.
3. Edit one of the files and save. Within about a second an `embedding …` log line shows the change
   re-embedded.
4. Stop the application, delete `.folderassistant/`, start again, and watch it rebuild.

To see retrieval quality rather than only the write side, run the labelled benchmark with Ollama
available — see [Benchmarks](#benchmarks).

## Configuration

All settings live under the `FolderAssistant` section of `FolderAssistant/appsettings.json`.
Environment variables (`FolderAssistant__Indexing__Enabled`) and user secrets work equally well, and
are the right place for anything you would not commit. For local development, copy the template:

```bash
cp FolderAssistant/appsettings.Development.template.json FolderAssistant/appsettings.Development.json
```

`appsettings.Development.json` is git-ignored and overrides `appsettings.json` in the Development
environment. Never commit a real API key; `FolderAssistant__Provider__ApiKey` as an environment
variable leaves nothing on disk.

The settings most likely to matter:

| Key | Default | Meaning |
|---|---|---|
| `Persistence:AnalyzedFolderPath` | working directory | The folder to index. |
| `Persistence:MetadataFolderName` | `.folderassistant` | Where the database goes, inside that folder. |
| `Profile` | `programmable-blob` | Which embedder, vector store and retrieval backend to compose. |
| `Port` | `5000` | HTTP listen port. |
| `Indexing:Enabled` | `true` | Set `false` to start the host without indexing. |
| `Indexing:MaxTextFileSizeBytes` | `1048576` | Larger files are skipped. |
| `Indexing:ChunkSizeTokens` / `ChunkOverlapTokens` | `256` / `32` | How text is split before embedding. |
| `Indexing:EmbeddingBatchSizeChunks` | `64` | Chunks per embed call. |
| `Indexing:DebounceMilliseconds` | `750` | Quiet window before a changed file is re-indexed. |
| `Indexing:ReconciliationIntervalSeconds` | `300` | How often the folder is re-scanned as a safety net. |
| `Indexing:FailedIndexRetryIntervalSeconds` | `30` | How often a failed first index is retried. |
| `Indexing:OllamaEndpoint` | `http://localhost:11434/v1` | Where the Ollama profiles embed. |
| `Indexing:OllamaModel` | `qwen3-embedding:0.6b` | The model they use. |
| `Indexing:OllamaTimeoutSeconds` | `120` | Deadline on one embed call. |

Every property, its default and its reason are documented in `FolderAssistant/AgentConfig.cs`.

### Choosing a profile

A **composition profile** bundles the embedder, the vector store and the retrieval backend together,
so a mismatched combination — a reader looking in one table while the writer fills another — cannot
be expressed. A profile you name that is unknown, or whose native dependency is missing on this
platform, stops the application at startup rather than falling back.

The name has two halves. The first is the **embedder**, which decides whether the right passage is
found at all; the second is the **store**, which decides how long that takes.

| Profile | Embedder | Retrieval | Needs |
|---|---|---|---|
| `ollama-vec` **(default)** | `qwen3-embedding:0.6b` via Ollama | `sqlite-vec` k-NN | Ollama running, on a covered platform |
| `ollama-blob` | `qwen3-embedding:0.6b` via Ollama | cosine, managed code | Ollama running |
| `lsa-vec` | LSA, fitted to the corpus, in-process | `sqlite-vec` k-NN | nothing on a covered platform |
| `lsa-blob` | LSA, fitted to the corpus, in-process | cosine, managed code | nothing |
| `programmable-vec` | character histogram (placeholder) | `sqlite-vec` k-NN | nothing on a covered platform |
| `programmable-blob` | character histogram (placeholder) | cosine, managed code | nothing |

**The default is the pretrained model, and it needs Ollama.** With no profile configured it is
`ollama-vec`. If Ollama is not installed, not running, or has not pulled the model, the startup probe
fails the index with a message saying which, and every search refuses until it is fixed. The
application never swaps in a different embedder in its place: a substitute would answer every question
from a different embedding space, plausibly and worse, and nothing would tell you. On the two
platforms where the `sqlite-vec` extension ships no binary — **Windows on ARM64, and musl-based Linux
such as Alpine** — the application runs `ollama-blob` instead: the same embedder over the brute-force
store, so the same search results, only slower on large folders. That substitution is never silent. It
is logged at warning on startup, and `GET /` reports the active profile with a note saying which one
was wanted. Name a profile explicitly and no substitution ever happens.

**On Linux** nothing special is needed beyond Ollama. The package ships `linux-x64` and `linux-arm64`
binaries, so Ubuntu, Debian, Fedora and the other glibc distributions run the default as-is; CI runs
the whole suite on Ubuntu for every push. Only Alpine and other musl builds take the store fallback.

**To run with nothing installed, name `lsa-vec`.** The corpus-fitted embedder is in-process and a long
way ahead of the placeholder, but a pretrained model is ahead of both. Measured on a 300-document
labelled corpus whose queries avoid the vocabulary of the documents they should find
([docs/benchmarks/semantic-search-full-results.md](docs/benchmarks/semantic-search-full-results.md)):

| Embedder | Precision@1 | MAP |
|---|---:|---:|
| `programmable-*` | 7 % | 0.031 |
| `lsa-*` | 45 % | 0.401 |
| `ollama-*` | 82 % | 0.674 |

All three rows were measured on 2026-09-16 on this tree, on Windows and again on Linux
([docs/benchmarks/linux/](docs/benchmarks/linux/)); the accuracy columns agree across the two to the
last digit, and the two stores agree with each other on every accuracy column on both.
[docs/benchmarks/2026-09-16-two-platforms.md](docs/benchmarks/2026-09-16-two-platforms.md) reads the
whole measurement, with charts: every profile, both stores, what the pretrained model costs per chunk,
and how the two stores diverge as a folder grows.

```json
{
  "FolderAssistant": {
    "Profile": "lsa-vec"
  }
}
```

Two things to know about the corpus-fitted profiles. The fit is taken on the first successful index
and kept, so a folder that grows a great deal after that searches with a fit taken on its early
contents until `.folderassistant/` is deleted and the index rebuilt; a refit policy is the next item
on the embedding side. And an empty folder cannot fit: the index reports failed until the folder
has text, and the pass is retried every 30 seconds.

Switching profiles changes the active model version; existing vectors are kept and simply stop being
the ones searched.

## Testing

```bash
dotnet test --nologo -l "console;verbosity=detailed" > "$TEMP/run.txt" 2>&1; tail -3 "$TEMP/run.txt"
```

Expect every test to pass. One test is skipped, with the reason printed, on a Windows machine
without Developer Mode: it needs a file symbolic link, which Windows grants only to an elevated
process. Run the suite to a file rather than through `-v q` or
`grep`: both discard the stack trace of a rare failure, and a re-run that passes takes the only copy
with it. Filtered runs work the usual way:

```bash
dotnet test --filter "FullyQualifiedName~FolderIndexingPipelineTests"
```

The suite is xUnit with FluentAssertions and Moq. Smoke tests boot the real host through
`WebApplicationFactory<Program>`, so they exercise database bootstrap and indexing end to end; a
cross-implementation contract suite holds both vector stores to one behaviour; and the indexing
library is tested against an in-memory outbox and against the real SQLite store.

Two things to know before trusting a green run:

- **Tests that need Ollama or the `sqlite-vec` native binary return early without asserting** when
  those are absent, because xUnit 2 has no dynamic skip. `OllamaLiveIntegrationTests` and
  `SqliteVecBackendTests` are the two suites, and they are a documented exception, not a convention.
- **The concurrent-bootstrap test used to go red about one run in five to ten.** It exposed a
  `Microsoft.Data.Sqlite` connection-pooling fault: one handle reached by two threads, about once in
  1,500 concurrent opens. Pooling is off on every connection since 2026-09-16, at a measured cost of
  about ten milliseconds on a retrieval over 24,000 vectors, mostly the page cache a pooled
  connection kept warm; the measurement and the rejected alternative are in
  [SPEC-130](docs/specs/SPEC-130-persistence.md). If that test goes red again with `SQLite Error 1`
  out of `BeginTransaction`, keep the log: it means a connection opened outside the factory.

### Benchmarks

Three opt-in benchmarks are measuring instruments under `[Fact]`. Each returns immediately and
asserts nothing unless its variable is set:

```bash
RUN_SEMANTIC_BENCHMARK=1 dotnet test --filter "FullyQualifiedName~SemanticSearchBenchmark" -l "console;verbosity=detailed"   # needs Ollama
BENCHMARK_FILES=4000     dotnet test --filter "FullyQualifiedName~CorpusBenchmark"         -l "console;verbosity=detailed"
RELEVANCE_FLOOR_BENCH=1  dotnet test --filter "FullyQualifiedName~RelevanceFloorBenchmark" -l "console;verbosity=detailed"
```

The first measures retrieval quality per profile on a hand-labelled corpus. The second measures
cold-start indexing throughput (`BENCHMARK_CORPUS=<path>` points it at a real folder). The third asks
whether a score floor can tell an answerable question from an unanswerable one. Results live in
[docs/benchmarks/](docs/benchmarks/), each dated to the tree that measured it; a figure is never
carried forward to a later tree.

### Troubleshooting

- **Startup fails naming a profile.** Either the name is not one of the six above, or a `-vec` profile
  was requested on a platform `sqlite-vec` has no binary for. Both are refused on purpose.

## How it is built

**Spec-first.** Every change that can alter behaviour, a data model, an interface or a workflow
starts from the affected spec in `docs/specs/` and ships the spec change in the same commit, so a spec
always reads as *what is true now*. The gate is
[.agent/skills/spec-alignment/SKILL.md](.agent/skills/spec-alignment/SKILL.md).

**Zero warnings, enforced.** `SonarAnalyzer.CSharp` runs on every build and `TreatWarningsAsErrors`
is set in `Directory.Build.props`. The four deliberate suppressions each carry a written justification
saying why the code is right, listed in [docs/ANALYZER-WARNINGS.md](docs/ANALYZER-WARNINGS.md). The
gate exists because a real defect once hid in analyzer noise here, in a fully green build no test
caught.

**Properties are mutation-tested.** A change that claims to close a race is checked by inverting or
removing its guard and confirming that exactly the tests written for it fail. The specs record the
kills.

**Measurements are re-taken, never quoted.** A commit stating a performance figure measures both
sides on the tree it produces, and compares trees from the same kind of directory.

**CI** ([.github/workflows/ci.yml](.github/workflows/ci.yml)) builds and tests in Release on both
`ubuntu-latest` and `windows-latest` for every push and pull request to `main`. Development is on
Windows, so the Linux job is where path-separator and case-sensitivity mistakes surface; the Windows
job runs the tests whose subject exists only there, such as `subst` drives, instead of skipping them.

## Architecture in brief

```
FolderAssistant/                the application: composition root, configuration, profiles
  Embedding/                    the vectorizer seam — histogram, corpus-fitted LSA, Ollama — and its telemetry decorator
  Indexing/                     scanner, chunker, whole-folder pass, bridge into the outbox library
  Persistence/                  SQLite bootstrap, connection factory, blob and sqlite-vec vector stores
  Retrieval/                    cosine and sqlite-vec queries, context reducer, low-confidence screen
  Tools/                        the containment guard, the read tools and the text search over it
src/FolderAssistant.Indexing/   the outbox indexer library: watcher, reconciler, change pipeline, dispatcher
FolderAssistant.Tests/          one test project for both assemblies, plus the opt-in benchmarks
docs/                           specifications, diagrams, benchmarks, archived designs
Directory.Packages.props        centrally managed package versions
Directory.Build.props           the zero-warning gate
```

A few decisions worth knowing before reading the code:

- **`Program.cs` is a composition root, not a script.** Configuration binds lazily through
  `IOptions<T>`; database bootstrap is ordered before the server listens by an `IStartupFilter`, so a
  test host that intercepts at `Build()` still gets a bootstrapped database.
- **The indexing library knows nothing of chunking, embedding or retrieval.** The boundary is
  one-way on purpose; the application implements the delivery seam the library calls through.
- **One type writes a file's record.** Whatever records a file writes the columns it owns, never the
  row it read back — a writer that reads a row, does something slow, then writes the whole row back
  silently reverts whatever another writer recorded in between. The store is built so that cannot be
  expressed.
- **Retrieval is lazy.** It will happen only when the model chooses to call a search tool, not as an
  unconditional pipeline stage, and it refuses while the first index is still building.
- **Telemetry lives in decorators over the composed seams**, so every call is recorded regardless of
  which backend a profile selected. "Still building" is classified apart from "the build failed",
  and a fault is classified by the caller's cancellation token before its exception type, because an
  HTTP client reports its own deadline as a cancellation.

[CLAUDE.md](CLAUDE.md) states every invariant with the reason it is there, and is written for anyone
changing the code.

## Conventions

Two conventions, one per assembly, enforced by `.editorconfig`. Match the file you are editing.

The application and the tests:

- Tabs for indentation.
- BCL type names spelled out — `String`, `Int32`, `Boolean` — rather than the C# keywords.
- `this.` on instance member access; private fields `_camelCase`.
- Braces on every block; `var` only where the type is already on the line.

The indexing library is standard modern C#: `string`, `var`, file-scoped namespaces.

Package versions are managed centrally; `.csproj` files reference packages without versions.
`NuGet.config` maps every package to nuget.org and nothing else.

## Documentation

- [`docs/specs/`](docs/specs/) — one versioned specification per module; the requirement every change
  is written from. Several are placeholders and say so. Start at
  [SPEC-000](docs/specs/SPEC-000-system-concept.md). Indexing is
  [SPEC-120](docs/specs/SPEC-120-rag-indexing.md) and
  [SPEC-121](docs/specs/SPEC-121-file-indexing-front-end.md); retrieval is
  [SPEC-110](docs/specs/SPEC-110-rag-retrieval.md); the storage choice and its alternatives are
  [SPEC-131](docs/specs/SPEC-131-database-options-analysis.md).
- [`docs/diagrams/`](docs/diagrams/) — the architecture as it is meant to hold together, and
  [implementation-status.md](docs/diagrams/implementation-status.md) for how much of it exists.
- [`docs/benchmarks/`](docs/benchmarks/) — retrieval quality, cold-start throughput and backend
  scaling, each dated to the tree that measured it.
- [`docs/observability-retrieval.md`](docs/observability-retrieval.md) — what the telemetry looks like
  and what to do about each status.
- [`docs/archive/`](docs/archive/) — designs considered and not built, kept for the reasoning that
  rejected them.
