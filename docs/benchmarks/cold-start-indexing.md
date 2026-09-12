# Cold start — a folder indexed for the first time

_Measured 2026-09-12 on this machine. Regenerate with `RUN_SEMANTIC_BENCHMARK=1 dotnet test --filter FullyQualifiedName~SemanticSearchBenchmark`._

The 20-document curated corpus, from nothing: creating the database, then the first full index. Split because the two are charged to different things — one is schema creation, the other is almost entirely embedding.

| Profile | Database (ms) | First index (ms) | Chunks | Per chunk (ms) |
|---|---:|---:|---:|---:|
| `programmable-blob` | 11 | 14 | 20 | 0.7 |
| `lsa-blob` | 11 | 109 | 20 | 5.5 |
| `ollama-blob` | 9 | 7715 | 20 | 385.8 |
