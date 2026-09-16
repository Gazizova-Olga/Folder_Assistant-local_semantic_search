# SPEC-100-conversation-orchestration — Conversation Orchestration

| | |
|---|---|
| Status | Draft — the composition root, the agent factory, the tool reflection and the facade written and implemented; routing, the roster and session persistence not |
| Version | 0.4.0 |
| Owner | Agents |
| Last updated | 2026-09-16 |

## Purpose

Routes a turn to an agent, runs it, and keeps the conversation's state across turns.

## Scope

**In scope**

- Agent roster and per-agent tool grants.
- Turn routing and execution.
- Session and history persistence boundaries.
- How a tool holder's methods become the functions a model calls, and how a tool's failure reaches
  the model or ends the turn.

**Out of scope**

- The provider transport itself (SPEC-140).
- Retrieval and indexing (SPEC-110, SPEC-120).
- What each tool does (SPEC-101).

## Requirements

Routing, the agent roster and session persistence are not built yet. What *is* built is the
composition root those things will be assembled in, the agent factory that makes one agent from the
configuration, and the reflection and facade that hand the tool holders' methods to it; the rules
below govern those now.

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

### The tools: reflected from the holders, one facade each

**A tool is a public instance method of a holder that carries a `[Description]`, named after the
method.** `ToolReflection.Reflect(holder)` yields one function per such method, in declaration order,
with the method's description as the tool's and each parameter's description in the schema; a
cancellation token parameter is bound by the loop and never shown to the model. Nothing is described
twice: the holder's attributes are the model-facing contract ([SPEC-101](SPEC-101-file-tools.md)), and
a method without one is not a tool. A holder that yields no tool throws, because a holder with nothing
to hand a model is a wiring mistake and not an agent with nothing to do.

**Every tool goes through one facade, and the facade applies the failure contract of its group.**
`ToolFacade` wraps a reflected function without changing its name, description or schema; it times
the call, writes one structured log line for it (`tool name=… group=… status=… latencyMs=…`, with the
exception type on a failure), and decides what a failure becomes:

- **A file tool's failure is a string.** The tool's exception becomes
  `TOOL_FAILED: <tool>: <message>` and the turn goes on. A missing file, a refused path, a bad line
  range is the model's mistake to correct or report, and ending the turn over it would end every turn
  that touched a wrong path. Logged at warning: the line is how an operator sees a model fumbling.
- **A search tool's failure is the exception itself, and the turn ends.** The facade lets it through
  unchanged — stack intact, the same instance — and logs it at error, the only error level here,
  because the turn ended on it. A swallowed retrieval fault is indistinguishable from "nothing
  relevant", and a model that believed it had searched would answer from prior knowledge. The index's
  own readiness refusal (`IndexNotReadyException`) is under the same rule: a refusal observed is not a
  licence to answer, and the caller of the turn is who is told the index is not ready.
- **A cancellation of the caller's token is neither.** It is classified by the token before the
  exception type — an HTTP client reports its own deadline as a cancellation — and passes through both
  groups, logged as cancelled: a string nobody will read is not a report. A cancellation nobody asked
  for is a failure under the group's contract.

**The group is decided by the holder's type, in one place.** `ToolSet.ForFiles(read, mutate, …)` wraps
the read holder and the mutation holder under the file contract; `ToolSet.ForSearch(search, …)` wraps
the search holder under the fatal one. A call site cannot put the search holder under the file contract
by getting an argument order wrong, which is the whole reason SPEC-101 built the holders apart.

**The other half of the fatal contract is the loop.** The framework's tool-calling loop catches a
tool that throws and hands the model a generic error string, then asks it again — exactly the
swallowed fault the search group refuses. So `AgentFactory` builds the loop itself, over the provider's
client, with **no tolerance for a failed iteration**: a tool that throws through its facade ends the
turn with its exception before the model is asked again. The file tools never throw through their
facade, so the setting reaches only the group it is meant for. The agent sees the loop is already
there and adds none of its own.

**The built prompt says what the failure string means.** The instructions built from the name and
description end with a sentence telling the model that a result beginning `TOOL_FAILED:` is a failure
to report as such and not to answer around. A configured prompt is still sent verbatim; an operator
who writes their own is responsible for that sentence.

### The agent factory

One agent is built from the configuration by `AgentFactory`, over the chat client the provider
factory made ([SPEC-140](SPEC-140-provider-adapters.md)). It is the agent framework's chat-client
agent, and four things about it are decided here:

- **The role is the configuration's.** `AgentName` and `AgentDescription` are the agent's name and
  description, with `Folder Assistant` and *answers questions about the contents of one folder on
  this machine* where they are unset. The instructions are `SystemPrompt` **verbatim** when one is
  set — nothing is appended to a prompt the operator wrote — and otherwise a prompt built from
  the name and description that tells the agent to answer from the folder through its tools, to say
  when they do not hold the answer, never to present what it did not find as if it came from the
  folder, and what a `TOOL_FAILED:` result means.
- **The tools are what it may call, and none means none.** The factory takes the tool list and hands
  it to the agent's chat options; an empty list leaves the options without tools rather than with an
  empty set. **Two tools with one name are refused** at construction: a model told of both could not
  say which it meant. The composition root builds the agent with every tool the three holders have,
  through `ToolSet`, so what exists is an agent that can read, search and change the folder; nothing
  runs a turn through it yet.
- **The loop is the factory's.** See above: built over the client with no tolerance for a failed
  iteration.
- **The handle owns the client, and says what the agent holds.** `AgentHandle` holds the agent and the
  client it talks through and disposes the client with itself; the framework's agent does not own its
  client, and a client is a disposable pipeline something has to end. Only synchronously disposable
  clients exist, so there is no async half. It also carries the tool list the agent was built with, so
  what an agent may call can be read without running a turn.

The composition root registers the client and the handle as factories, resolved by nothing yet, so a
host boots with a provider it cannot reach and a configuration that cannot name one fails when the
handle is first asked for — with the sentence SPEC-140 specifies — never at boot.

## Contracts

```csharp
internal static class ToolReflection
{
	static IReadOnlyList<AIFunction> Reflect(Object holder);   // throws when nothing is described
}

internal enum ToolGroup { File, Search }

internal sealed class ToolFacade : DelegatingAIFunction
{
	const String FailurePrefix = "TOOL_FAILED: ";
	ToolFacade(AIFunction inner, ToolGroup group, ILogger logger);
	ToolGroup Group { get; }
}

internal static class ToolSet
{
	static IReadOnlyList<AITool> ForFiles(ReadTools read, MutationTools mutate, ILogger logger);
	static IReadOnlyList<AITool> ForSearch(SearchTools search, ILogger logger);
}

internal static class AgentFactory
{
	static AgentHandle Create(AgentConfig config, IChatClient client, IReadOnlyList<AITool> tools, ILoggerFactory? loggerFactory = null);
	static IChatClient WithFunctionInvocation(IChatClient client, ILoggerFactory? loggerFactory);
	static String Instructions(AgentConfig config, String name, String description);
}

internal sealed class AgentHandle : IDisposable
{
	String Name { get; }
	AIAgent Agent { get; }
	IReadOnlyList<AITool> Tools { get; }
}
```

## Non-functional requirements

- Reliability:
- Performance:
- Operability: every tool call is one structured log line naming the tool, its group, how it ended and
  how long it took; a file failure at warning, a search failure at error, a cancellation and a success
  at information.

## Test strategy

- **Host startup (implemented)** — a configuration source added after composition reaches the
  running application; the database is bootstrapped before any request is served; disabling
  indexing leaves the index `Ready` *and* indexes nothing, which is what separates it from a
  pass that merely finished.
- Reverting the binding to an eager one fails three of those four. That is the only reason to
  trust the rule above rather than take it on faith.
- **The reflection** — each holder yields exactly its described methods, in declaration order, named
  after them; a function carries the holder's descriptions and not the cancellation token; a reflected
  function called with arguments really calls the holder and returns its record; a holder with no
  described method is refused; an undescribed public method is not a tool.
- **The facade, one test per contract** — a file tool's failure is the prefixed string naming the tool
  and the cause, logged once at warning with the exception; a search tool's failure is the same
  exception instance, logged once at error; a success passes through with the name, description and
  schema unchanged and one line at information; a cancellation of the caller's token passes through
  both groups, logged as cancelled; a cancellation nobody asked for is a failure.
- **The loop, through a real agent over a scripted client** that "calls" a tool and then answers: a
  file tool's failure reaches the model as the string in the function-result message and the model's
  next turn is the answer; a search tool's failure ends the turn with the tool's own exception and the
  model is not asked again. The second is the test the framework's default fails.
- **The agent factory** — over a recording chat client: the configured prompt reaches the model
  verbatim and the name is the handle's; without a prompt one is built from the name and description
  or their defaults, and says what the failure prefix means; the tools reach the model and an empty
  list sends none; two tools with one name are refused; disposing the handle disposes the client; the
  client's sampling defaults reach every call. A host test resolves the handle from the real root over
  a local endpoint with no network, asserts it holds all ten tools with the search holder's alone under
  the fatal group, and that a keyless hosted configuration fails when asked for, not at boot.
- Mutation kills, each restored byte-for-byte: the loop left at its default fails exactly the
  turn-ending test; the facade handling both groups as strings fails the search contract and the
  turn-ending test; the token check inverted fails the three cancellation tests; the description
  filter removed from the reflection fails the two tests that name it. A type-only classification
  could not be written as a runtime mutant — the analyzer refuses the always-true clause — so the
  inverted check is the evidence that the token, not the type, decides.

Not covered: routing, the roster and session persistence, because they are not built yet.

## Open questions

## References

- [System concept](SPEC-000-system-concept.md)
- [SPEC-101 — File tools](SPEC-101-file-tools.md)
- [SPEC-140 — Provider adapters](SPEC-140-provider-adapters.md)

## Changelog

- **0.4.0** (2026-09-16) — the tool reflection, the facade with its two contracts, the loop built to
  tolerate no failed iteration, the duplicate-name refusal, the failure sentence in the built prompt,
  and the handle's tool list; written with their implementation.
- **0.3.0** (2026-09-16) — the agent factory and the handle, written with their implementation.
- **0.2.0** (2026-09-09) — the composition root's rules: lazy binding, startup order.
