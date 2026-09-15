# Semantic search — generated corpus and backend scaling

_Measured 2026-09-15 on this machine. Regenerate with `RUN_SEMANTIC_BENCHMARK=1 dotnet test --filter FullyQualifiedName~SemanticSearchBenchmark`._

300 documents built from the topic seeds, several per topic, each pairing differently worded variants of the same subject. Every document of a topic counts as relevant to that topic's queries, which is what makes the multi-relevant metrics below meaningful.

| Profile | dim | P@1 | MAP | nDCG@10 | Recall@10 |
|---|---:|---:|---:|---:|---:|
| `programmable-blob` | 64 | 7 % | 0.031 | 0.071 | 7 % |
| `lsa-blob` | 32 | 45 % | 0.401 | 0.451 | 44 % |

_MAP is the headline number where a query has many relevant documents._

## Backend latency as the corpus grows

Same embedder throughout — how long a k-NN read takes is a property of the store, not of what produced the numbers in it.

| Docs | Profile | Index (s) | Query p50 (ms) | Query p95 (ms) | DB |
|---:|---|---:|---:|---:|---:|
| 500 | `programmable-blob` | 0.2 | 2.4 | 5.0 | 932 KB |
| 500 | `programmable-vec` | 0.2 | 0.6 | 1.1 | 1052 KB |
| 2000 | `programmable-blob` | 0.8 | 10.2 | 14.7 | 3460 KB |
| 2000 | `programmable-vec` | 0.8 | 0.9 | 2.2 | 3284 KB |
| 8000 | `programmable-blob` | 3.2 | 58.0 | 82.8 | 13600 KB |
| 8000 | `programmable-vec` | 3.8 | 2.8 | 4.4 | 12796 KB |

**Not re-measured on this tree:** the `ollama-blob` row. Ollama is not installed on the machine that took
these figures. On 2026-09-12, on a different machine and an earlier tree, it measured P@1 82 %, MAP 0.675,
nDCG@10 0.748, Recall@10 72 %; those figures are of that tree and that machine and are not carried into
the table above.
