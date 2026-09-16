# Cold start — a folder indexed for the first time

_Measured 2026-09-16 on this machine: Ubuntu 24.04.5 LTS, X64, 8 logical processors. Regenerate with `RUN_SEMANTIC_BENCHMARK=1 dotnet test --filter FullyQualifiedName~SemanticSearchBenchmark`._

The 20-document curated corpus, from nothing: creating the database, then the first full index. Split because the two are charged to different things — one is schema creation, the other is almost entirely embedding.

| Profile | Database (ms) | First index (ms) | Chunks | Per chunk (ms) |
|---|---:|---:|---:|---:|
| `programmable-blob` | 18 | 37 | 20 | 1.9 |
| `lsa-blob` | 11 | 65 | 20 | 3.2 |
| `ollama-blob` | 12 | 13150 | 20 | 657.5 |
