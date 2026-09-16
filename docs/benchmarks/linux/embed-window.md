# What the embed window buys

_Measured 2026-09-16 on this machine: Ubuntu 24.04.5 LTS, X64, 8 logical processors. Regenerate with `RUN_SEMANTIC_BENCHMARK=1 dotnet test --filter FullyQualifiedName~SemanticSearchBenchmark`._

The 20-document curated corpus indexed from nothing at each window, against a local embedding server. One window size means a call per chunk, which is what the pipeline did before it gathered chunks across files. 3 passes, alternating between windows so that machine drift lands on all of them rather than on whichever ran last.

| Window | Median (ms) | Passes (ms) | Against a call per chunk |
|---:|---:|---|---:|
| 1 | 14569 | 14569, 15513, 11785 | 1.00x |
| 8 | 12568 | 14694, 12282, 12568 | 1.16x |
| 64 | 13932 | 17418, 11510, 13932 | 1.05x |

_The spread across passes is the thing to read before the ratio: where the passes overlap, the medians are not separated by this many samples._
