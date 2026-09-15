# Cold start — a folder indexed for the first time

_Measured 2026-09-15 on this machine. Regenerate with `RUN_SEMANTIC_BENCHMARK=1 dotnet test --filter FullyQualifiedName~SemanticSearchBenchmark`._

The 20-document curated corpus, from nothing: creating the database, then the first full index. Split because the two are charged to different things — one is schema creation, the other is almost entirely embedding.

| Profile | Database (ms) | First index (ms) | Chunks | Per chunk (ms) |
|---|---:|---:|---:|---:|
| `programmable-blob` | 9 | 32 | 20 | 1.6 |
| `lsa-blob` | 9 | 51 | 20 | 2.5 |

**Not re-measured on this tree:** the `ollama-blob` row. On 2026-09-12, on a different machine and an
earlier tree, its first index of the same corpus took 7715 ms, 385.8 ms per chunk; that figure is of that
tree and that machine and is not carried into the table above.
