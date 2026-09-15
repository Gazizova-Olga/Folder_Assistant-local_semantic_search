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
| Storage | SQLite via `Microsoft.Data.Sqlite`, WAL mode, optional `sqlite-vec` native k-NN |
| Observability | OpenTelemetry metrics exported for Prometheus at `GET /metrics`; structured logging |
| Quality gates | Zero-warning build enforced with SonarAnalyzer; ~500 xUnit tests; CI on Ubuntu and Windows for every push |
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
- **Reading is not writing.** The planned file tools are split into read and mutation holders, so
  which agent can change data will be answerable from one line of configuration.

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
- **Telemetry** — a structured log line per embed call, and retrieval metrics at `GET /metrics`.

**Built and tested, not yet reachable at runtime**

- Retrieval — cosine ranking within one embedding space, in managed code or through `sqlite-vec` —
  and the context reducer that reranks, diversifies and fits passages under a token budget. Nothing
  on the request path calls them yet, so the vectors are written and read only by the test suite.
- The **workspace path guard** every file tool will resolve paths through: it refuses anything
  outside the folder, including escapes through symbolic links and junctions, hard links, and
  `subst` drives, and refuses the index's own metadata folder for every operation.

**Not built**

- The filesystem tools, the agent and provider adapter, the chat surface, the conversation database.

In practice: you can run the application today to **index a folder, watch it follow your edits and
read the telemetry**. You cannot yet ask it a question.

## Quick start

### Requirements

- **.NET 10 SDK** — `dotnet --version` prints `10.x`.
- **Ollama**, optional but needed for real semantic search. Install it, keep it running, and pull the
  embedding model:

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
| `GET /` | The application name and the absolute path of the folder being indexed. |
| `GET /metrics` | Prometheus text: retrieval counters and latency histograms, per backend. |

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
still building, and is retried every 30 seconds.

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
be expressed. An unknown profile, or one whose native dependency is missing on this platform, stops
the application at startup rather than falling back.

| Profile | Embedder | Retrieval | Needs |
|---|---|---|---|
| `programmable-blob` (default) | character histogram | cosine, managed code | nothing |
| `programmable-vec` | character histogram | `sqlite-vec` k-NN | native `sqlite-vec` binary |
| `lsa-blob` | LSA, fitted to the corpus | cosine, managed code | nothing |
| `lsa-vec` | LSA, fitted to the corpus | `sqlite-vec` k-NN | native `sqlite-vec` binary |
| `ollama-blob` | `qwen3-embedding:0.6b` via Ollama | cosine, managed code | Ollama running |
| `ollama-vec` | `qwen3-embedding:0.6b` via Ollama | `sqlite-vec` k-NN | Ollama + native binary |

**The default is the profile with no dependencies, not the one that retrieves best.** The
placeholder embedder is a deterministic floor with no semantics. Measured on a 300-document labelled
corpus whose queries avoid the vocabulary of the documents they should find
([docs/benchmarks/semantic-search-full-results.md](docs/benchmarks/semantic-search-full-results.md)):

| Profile | Precision@1 | MAP |
|---|---:|---:|
| `programmable-blob` | 7 % | 0.031 |
| `lsa-blob` | 45 % | 0.401 |
| `ollama-blob` | 82 % | 0.675 |

For any real use, run Ollama and set the profile:

```json
{
  "FolderAssistant": {
    "Profile": "ollama-blob"
  }
}
```

The `-vec` variants use the native `sqlite-vec` extension, which ships no binary for win-arm64 or
musl-based Linux; the benchmark document shows where it pays off as the corpus grows. Switching
profiles changes the active model version; existing vectors are kept and simply stop being the ones
searched.

## Testing

```bash
dotnet test --nologo -l "console;verbosity=detailed" > "$TEMP/run.txt" 2>&1; tail -3 "$TEMP/run.txt"
```

Expect every test to pass with none skipped. Run the suite to a file rather than through `-v q` or
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
- **One test is known to go red about one run in five to ten.** The concurrent-bootstrap test in
  `PersistenceConcurrencyTests` exposes a `Microsoft.Data.Sqlite` connection-pooling fault that was
  measured at 70 failures in 100,000 pooled bootstraps and 0 in 40,000 unpooled. Both candidate fixes
  are written up in [SPEC-130](docs/specs/SPEC-130-persistence.md); the choice is deliberately open.
  The test keeps its concurrency because that is what makes the property testable.

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
