# Linux measurements

The four reports in this folder are the same instrument as the ones beside it, run on Linux. The
files beside this folder are measured on the development machine, Windows; these are measured on the
same physical machine under WSL2, so the two sets compare an operating system, a kernel and a
filesystem, not two computers.

How they were produced, 2026-09-16:

- A Podman machine (WSL2, Fedora-based, 8 logical processors) on the Windows development laptop.
- One pod holding two containers: `docker.io/ollama/ollama:latest` serving `qwen3-embedding:0.6b`
  on the CPU, and `mcr.microsoft.com/dotnet/sdk:10.0` (Ubuntu 24.04, .NET SDK 10.0.401) holding a
  copy of this tree. Sharing the pod is what makes `localhost:11434` the model server inside the SDK
  container, so no configuration differs from the Windows run.
- `RUN_SEMANTIC_BENCHMARK=1 dotnet test --filter FullyQualifiedName~SemanticSearchBenchmark` in the
  SDK container; the reports were copied out unchanged.

Read the per-chunk embedding figure with that in mind: it is a CPU-only server inside a virtual
machine, and it is compared against a CPU-only server on the host. Neither is what a machine with a
GPU would show.
