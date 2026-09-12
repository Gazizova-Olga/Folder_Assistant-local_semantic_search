# Semantic search — curated corpus

_Measured 2026-09-12 on this machine. Regenerate with `RUN_SEMANTIC_BENCHMARK=1 dotnet test --filter FullyQualifiedName~SemanticSearchBenchmark`._

20 hand-written documents, 22 hand-labelled queries. Each query is worded to avoid the distinctive vocabulary of the document it should find, so ranking it correctly takes meaning rather than shared words.

| Profile | dim | Recall@1 | Recall@3 | Recall@5 | MRR@10 | Index (ms) | Mean query (ms) | DB |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `programmable-blob` | 64 | 0 % | 5 % | 14 % | 0.105 | 11 | 0.2 | 92 KB |
| `ollama-blob` | 1024 | 100 % | 100 % | 100 % | 1.000 | 6894 | 97.4 | 176 KB |
| `programmable-vec` | 64 | 0 % | 5 % | 14 % | 0.105 | 19 | 0.8 | 388 KB |
| `ollama-vec` | 1024 | 100 % | 100 % | 100 % | 1.000 | 7101 | 114.8 | 4232 KB |

_Recall@k is the share of queries whose correct document appears in the top k. MRR@10 is the mean reciprocal rank of the first correct document._
