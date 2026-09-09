# SPEC-100-conversation-orchestration — Conversation Orchestration

| | |
|---|---|
| Status | Draft — partly written |
| Version | 0.2.0 |
| Owner | — |
| Last updated | 2026-09-09 |

## Purpose

Routes a turn to an agent, runs it, and keeps the conversation's state across turns.

## Scope

**In scope**

- Agent roster and per-agent tool grants.
- Turn routing and execution.
- Session and history persistence boundaries.

**Out of scope**

- The provider transport itself (SPEC-140).
- Retrieval and indexing (SPEC-110, SPEC-120).

## Requirements

Most of this document is still a placeholder — routing, the agent roster and session
persistence are not built yet. What *is* built is the composition root those things will be
assembled in, and the rules below govern it now.

### Configuration is bound lazily

**Configuration binds through `IOptions<AgentConfig>`. It is never read eagerly off
`builder.Configuration` while the composition root runs.** Everything depending on it — the
database bootstrap, the indexing hosted service, and in time the agent — is registered as a
factory that resolves after the host is built.

An eager bind freezes the values before the host exists, which silently discards every
configuration source added afterwards. A test host adds exactly such a source, so an
eagerly-bound application ignores what a test asked for and runs against whatever the
developer's own settings happen to say — with no error, and a plausible-looking result.

**Only host-level values may be read early**, because they cannot be late-bound. Today that is
one: the port Kestrel binds on. It does not change behaviour.

### Startup order

The host is built, and then an `IStartupFilter` forces the database bootstrap
([SPEC-130](SPEC-130-persistence.md)) into existence before the server begins listening.
Indexing starts as a background hosted service and the host does not wait for it
([SPEC-120](SPEC-120-rag-indexing.md)).

The ordering cannot be done by running the bootstrap at the end of `Main` instead. A test host
intercepts at `builder.Build()`, so nothing after that line runs under test — the guarantee
would hold in production and silently not hold everywhere it is checked.

## Contracts

## Non-functional requirements

- Reliability:
- Performance:
- Operability:

## Test strategy

- **Host startup (implemented)** — a configuration source added after composition reaches the
  running application; the database is bootstrapped before any request is served; disabling
  indexing leaves the index `Ready` *and* indexes nothing, which is what separates it from a
  pass that merely finished.
- Reverting the binding to an eager one fails three of those four. That is the only reason to
  trust the rule above rather than take it on faith.

Not covered: everything else in this document, because it is not built yet.

## Open questions

## References

- [System concept](SPEC-000-system-concept.md)
