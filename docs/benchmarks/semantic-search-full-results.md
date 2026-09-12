# Semantic search — generated corpus and backend scaling

_Measured 2026-09-12 on this machine. Regenerate with `RUN_SEMANTIC_BENCHMARK=1 dotnet test --filter FullyQualifiedName~SemanticSearchBenchmark`._

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
| 500 | `programmable-blob` | 0.1 | 1.4 | 2.0 | 764 KB |
| 500 | `programmable-vec` | 0.1 | 0.3 | 0.5 | 888 KB |
| 2000 | `programmable-blob` | 0.4 | 6.0 | 7.3 | 2844 KB |
| 2000 | `programmable-vec` | 0.5 | 0.5 | 1.4 | 2672 KB |
| 8000 | `programmable-blob` | 2.4 | 34.1 | 47.0 | 11156 KB |
| 8000 | `programmable-vec` | 2.8 | 1.7 | 3.9 | 10352 KB |
