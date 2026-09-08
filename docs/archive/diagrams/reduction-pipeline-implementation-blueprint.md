# Reduction Pipeline (Service Variant) — Superseded

> **Not built.** This was the reduction pipeline as it would have looked split across
> services. It is kept alongside the [microservice architecture](microservice-architecture.md)
> it belonged to. The version that exists is
> [the library variant](../../diagrams/reduction-pipeline-library-implementation-blueprint.md).

## What it proposed

The same five stages — ingest, embed, store, retrieve, converse — with a network boundary
between each, coordinated by a broker.

### Index-time

1. A file watcher service publishes a change event.
2. The indexing service consumes it, hashes the file, and publishes a chunking request.
3. The chunking worker splits the file and publishes one embedding request per chunk.
4. The embedding service returns vectors.
5. The indexing service writes the manifest, the chunks and the vectors — each through its
   owning service's API.

### Request-time

1. The conversation service receives a turn.
2. It calls the retrieval service.
3. Retrieval calls the embedding service for the query vector.
4. Retrieval calls the vector store for candidates, ranks them, assembles context.
5. The conversation service calls the language model with that context.

## Why it did not survive

**Step 5 of index-time is the problem.** In one process, that is a single transaction over
three tables. Split across services it becomes a distributed write with compensating
actions, guarding against a failure — a chunk whose vector never landed — whose only
symptom is that the chunk quietly never matches anything. The complexity is real and
immediate; the protection is against something the simpler design makes impossible.

**Request-time pays two network round trips before ranking begins**, on the path a user is
actually waiting on.

**The event choreography is elaborate for a corpus that fits on one disk.** Four hops from
"a file changed" to "a vector is stored", each with its own retry and ordering semantics,
replacing what is otherwise a function call.

## What was worth keeping

The stage decomposition, which is the same in the built system. And one specific idea:
**the embedding model should load once**. That survived not as a service boundary but as an
option — a locally running embedding server that several processes can share, described in
[SPEC-162](../../specs/SPEC-162-embedding-ollama-local.md).

## References

- [Microservice architecture (superseded)](microservice-architecture.md)
- [Reduction pipeline, library variant](../../diagrams/reduction-pipeline-library-implementation-blueprint.md)
