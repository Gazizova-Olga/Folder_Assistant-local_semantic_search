# What the embed window buys

_Measured 2026-09-12 on this machine. Regenerate with `RUN_SEMANTIC_BENCHMARK=1 dotnet test --filter FullyQualifiedName~SemanticSearchBenchmark`._

The 20-document curated corpus indexed from nothing at each window, against a local embedding server. One window size means a call per chunk, which is what the pipeline did before it gathered chunks across files. 3 passes, alternating between windows so that machine drift lands on all of them rather than on whichever ran last.

| Window | Median (ms) | Passes (ms) | Against a call per chunk |
|---:|---:|---|---:|
| 1 | 10959 | 10458, 11049, 10959 | 1.00x |
| 8 | 10527 | 10313, 10536, 10527 | 1.04x |
| 64 | 10519 | 10940, 10519, 10518 | 1.04x |

_The spread across passes is the thing to read before the ratio: where the passes overlap, the medians are not separated by this many samples._
