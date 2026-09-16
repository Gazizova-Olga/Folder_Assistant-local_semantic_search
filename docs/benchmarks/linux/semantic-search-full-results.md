# Semantic search — generated corpus and backend scaling

_Measured 2026-09-16 on this machine: Ubuntu 24.04.5 LTS, X64, 8 logical processors. Regenerate with `RUN_SEMANTIC_BENCHMARK=1 dotnet test --filter FullyQualifiedName~SemanticSearchBenchmark`._

300 documents built from the topic seeds, several per topic, each pairing differently worded variants of the same subject. Every document of a topic counts as relevant to that topic's queries, which is what makes the multi-relevant metrics below meaningful.

| Profile | dim | P@1 | MAP | nDCG@10 | Recall@10 |
|---|---:|---:|---:|---:|---:|
| `programmable-blob` | 64 | 7 % | 0.031 | 0.071 | 7 % |
| `lsa-blob` | 32 | 45 % | 0.401 | 0.451 | 44 % |
| `ollama-blob` | 1024 | 82 % | 0.675 | 0.748 | 72 % |

_MAP is the headline number where a query has many relevant documents._

## Backend latency as the corpus grows

Same embedder throughout — how long a k-NN read takes is a property of the store, not of what produced the numbers in it.

| Docs | Profile | Index (s) | Query p50 (ms) | Query p95 (ms) | DB |
|---:|---|---:|---:|---:|---:|
| 500 | `programmable-blob` | 0.2 | 3.1 | 5.7 | 932 KB |
| 500 | `programmable-vec` | 0.2 | 0.5 | 0.7 | 1052 KB |
| 2000 | `programmable-blob` | 0.7 | 11.0 | 15.6 | 3432 KB |
| 2000 | `programmable-vec` | 0.8 | 0.7 | 1.1 | 3260 KB |
| 8000 | `programmable-blob` | 2.6 | 52.1 | 60.7 | 13576 KB |
| 8000 | `programmable-vec` | 3.3 | 2.6 | 3.0 | 12752 KB |
