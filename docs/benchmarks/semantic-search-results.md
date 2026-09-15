# Semantic search — curated corpus

_Measured 2026-09-15 on this machine. Regenerate with `RUN_SEMANTIC_BENCHMARK=1 dotnet test --filter FullyQualifiedName~SemanticSearchBenchmark`._

20 hand-written documents, 22 hand-labelled queries. Each query is worded to avoid the distinctive vocabulary of the document it should find, so ranking it correctly takes meaning rather than shared words.

| Profile | dim | Recall@1 | Recall@3 | Recall@5 | MRR@10 | Index (ms) | Mean query (ms) | DB |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `programmable-blob` | 64 | 0 % | 5 % | 14 % | 0.105 | 52 | 0.1 | 108 KB |
| `lsa-blob` | 20 | 18 % | 36 % | 45 % | 0.334 | 122 | 0.4 | 300 KB |
| `programmable-vec` | 64 | 0 % | 5 % | 14 % | 0.105 | 34 | 0.8 | 400 KB |
| `lsa-vec` | 20 | 18 % | 36 % | 45 % | 0.334 | 61 | 0.9 | 424 KB |

_Recall@k is the share of queries whose correct document appears in the top k. MRR@10 is the mean reciprocal rank of the first correct document._

**Not re-measured on this tree:** the `ollama-*` rows. Ollama is not installed on the machine that took
these figures. On 2026-09-12, on a different machine and an earlier tree, `ollama-blob` and `ollama-vec`
both measured Recall@1 100 %, MRR 1.000, with a mean query of 97–115 ms and a first index of ~7 s; those
figures are of that tree and that machine and are not carried into the table above.
