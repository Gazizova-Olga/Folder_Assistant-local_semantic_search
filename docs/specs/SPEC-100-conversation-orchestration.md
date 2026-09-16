# SPEC-100-conversation-orchestration — Conversation Orchestration

| | |
|---|---|
| Status | Draft — the composition root and the agent factory written and implemented; routing, the roster and session persistence not |
| Version | 0.3.0 |
| Owner | Agents |
| Last updated | 2026-09-16 |

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
assembled in, and the agent factory that makes one agent from the configuration; the rules below
govern both now.

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

### The agent factory

One agent is built from the configuration by `AgentFactory`, over the chat client the provider
factory made ([SPEC-140](SPEC-140-provider-adapters.md)). It is the agent framework's chat-client
agent, and three things about it are decided here:

- **The role is the configuration's.** `AgentName` and `AgentDescription` are the agent's name and
  description, with `Folder Assistant` and *answers questions about the contents of one folder on
  this machine* where they are unset. The instructions are `SystemPrompt` **verbatim** when one is
  set — nothing is appended to a prompt the operator wrote — and otherwise a prompt built from
  the name and description that tells the agent to answer from the folder through its tools, to say
  when they do not hold the answer, and never to present what it did not find as if it came from the
  folder.
- **The tools are what it may call, and none means none.** The factory takes the tool list and hands
  it to the agent's chat options; an empty list leaves the options without tools rather than with an
  empty set. Today the composition root builds the agent with no tools: what is reflected in, and
  how a tool failure reaches the model, is the work that follows.
- **The handle owns the client.** `AgentHandle` holds the agent and the client it talks through and
  disposes the client with itself; the framework's agent does not own its client, and a client is a
  disposable pipeline something has to end. Only synchronously disposable clients exist, so there is
  no async half.

The composition root registers the client and the handle as factories, resolved by nothing yet, so a
host boots with a provider it cannot reach and a configuration that cannot name one fails when the
handle is first asked for — with the sentence SPEC-140 specifies — never at boot.

## Contracts

```csharp
internal static class AgentFactory
{
	static AgentHandle Create(AgentConfig config, IChatClient client, IReadOnlyList<AITool> tools, ILoggerFactory? loggerFactory = null);
	static String Instructions(AgentConfig config, String name, String description);
}

internal sealed class AgentHandle : IDisposable
{
	String Name { get; }
	AIAgent Agent { get; }
}
```

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
- **The agent factory** — over a recording chat client: the configured prompt reaches the model
  verbatim and the name is the handle's; without a prompt one is built from the name and description
  or their defaults; the tools reach the model and an empty list sends none; disposing the handle
  disposes the client; the client's sampling defaults reach every call. A host test resolves the
  handle from the real root over a local endpoint with no network, and a keyless hosted
  configuration fails when asked for, not at boot.

Not covered: routing, the roster and session persistence, because they are not built yet.

## Open questions

## References

- [System concept](SPEC-000-system-concept.md)
- [SPEC-140 — Provider adapters](SPEC-140-provider-adapters.md)

## Changelog

- **0.3.0** (2026-09-16) — the agent factory and the handle, written with their implementation.
- **0.2.0** (2026-09-09) — the composition root's rules: lazy binding, startup order.
