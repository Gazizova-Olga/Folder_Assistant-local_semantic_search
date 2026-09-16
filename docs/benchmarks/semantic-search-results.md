# Semantic search — curated corpus

_Measured 2026-09-16 on this machine: Microsoft Windows 10.0.26200, X64, 8 logical processors. Regenerate with `RUN_SEMANTIC_BENCHMARK=1 dotnet test --filter FullyQualifiedName~SemanticSearchBenchmark`._

20 hand-written documents, 22 hand-labelled queries. Each query is worded to avoid the distinctive vocabulary of the document it should find, so ranking it correctly takes meaning rather than shared words.

| Profile | dim | Recall@1 | Recall@3 | Recall@5 | MRR@10 | Index (ms) | Mean query (ms) | DB |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `programmable-blob` | 64 | 0 % | 5 % | 14 % | 0.105 | 47 | 0.2 | 108 KB |
| `lsa-blob` | 20 | 18 % | 36 % | 45 % | 0.334 | 123 | 0.1 | 300 KB |
| `ollama-blob` | 1024 | 95 % | 100 % | 100 % | 0.977 | 13203 | 173.9 | 192 KB |
| `programmable-vec` | 64 | 0 % | 5 % | 14 % | 0.105 | 52 | 0.7 | 400 KB |
| `lsa-vec` | 20 | 18 % | 36 % | 45 % | 0.334 | 72 | 0.5 | 424 KB |
| `ollama-vec` | 1024 | 95 % | 100 % | 100 % | 0.977 | 11983 | 175.0 | 4244 KB |

_Recall@k is the share of queries whose correct document appears in the top k. MRR@10 is the mean reciprocal rank of the first correct document._
