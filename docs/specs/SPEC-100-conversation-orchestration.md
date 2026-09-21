# SPEC-100-conversation-orchestration — Conversation Orchestration

| | |
|---|---|
| Status | Draft — the composition root, the agent factory, the tool reflection and facade, the roster, the catalog, the registry and the route written and implemented; turn execution and session persistence not |
| Version | 0.5.0 |
| Owner | Agents |
| Last updated | 2026-09-18 |

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

Turn execution and session persistence are not built yet. What *is* built is the composition root
those things will be assembled in, the agent factory that makes one agent from a role, the reflection
and facade that hand the tool holders' methods to it, and the roster: which agents exist, what each
may call and delegate to, and which one every turn enters. The rules below govern those now.

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
([SPEC-130](SPEC-130-persistence.md)) and the roster's validation (below) into existence before the
server begins listening. Indexing starts as a background hosted service and the host does not wait
for it ([SPEC-120](SPEC-120-rag-indexing.md)).

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
  unchanged — stack intact, the same instance — and logs it at error, because the turn ended on it. A
  swallowed retrieval fault is indistinguishable from "nothing relevant", and a model that believed it
  had searched would answer from prior knowledge. The index's own readiness refusal
  (`IndexNotReadyException`) is under the same rule: a refusal observed is not a licence to answer,
  and the caller of the turn is who is told the index is not ready.
- **A delegation's failure is the exception itself, and the turn ends** — the same contract one level
  up. A delegate whose turn failed has nothing to report, and the caller answering anyway would be the
  same plausible wrong answer.
- **A cancellation of the caller's token is neither.** It is classified by the token before the
  exception type — an HTTP client reports its own deadline as a cancellation — and passes through every
  group, logged as cancelled: a string nobody will read is not a report. A cancellation nobody asked
  for is a failure under the group's contract.

**The group is decided by the holder's type, in one place.** `ToolSet.ForFiles(read, mutate, …)` wraps
the read holder and the mutation holder under the file contract; `ToolSet.ForSearch(search, …)` wraps
the search holder under the fatal one; the registry wraps each delegation it builds under the
delegation group. A call site cannot put the search holder under the file contract by getting an
argument order wrong, which is the whole reason SPEC-101 built the holders apart.

**The other half of the fatal contract is the loop.** The framework's tool-calling loop catches a
tool that throws and hands the model a generic error string, then asks it again — exactly the
swallowed fault the search group refuses. So `AgentFactory` builds the loop itself, over the provider's
client, with **no tolerance for a failed iteration**: a tool that throws through its facade ends the
turn with its exception before the model is asked again. The file tools never throw through their
facade, so the setting reaches only the groups it is meant for. The agent sees the loop is already
there and adds none of its own.

**The built prompt says what the failure string means.** The instructions built from the name and
description end with a sentence telling the model that a result beginning `TOOL_FAILED:` is a failure
to report as such and not to answer around. A configured prompt is still sent verbatim; an operator
who writes their own is responsible for that sentence.

### The roster

**The roster is which agents exist, what each may call and delegate to, and which one every turn
enters** — `Roster`, built once from the configuration and validated whole at startup. Three rosters
are possible, and they take precedence in this order:

1. **The operator's.** Any entry under `Workflow:Agents` makes those entries the roster, whole. Each
   entry has a `Name` (unique, required), a `Description` (required: it is what a delegation to the
   agent is described by), an optional `SystemPrompt`, `Tools` (an allowlist of tool names; none means
   none), `Delegates` (agent names; one delegation tool per target) and an optional `Provider`.
2. **The default roster, from code**, when `Workflow:UseDefaultRoster` is on and no entries are
   configured: `orchestrator` with no tools, delegating to `reader` and `mutator`; `reader` holding
   `InspectDirectory`, `ReadFile`, `Retrieve`, `FindFiles`, `SearchText` and `FindFilesAbout`;
   `mutator` holding `ReadFile`, `Retrieve`, `Create`, `Update`, `ReplaceLines` and `Delete`. The split
   is the point: which agent can change the folder is one line here.
3. **One agent from the root configuration** otherwise — `AgentName`, `AgentDescription` and
   `SystemPrompt` with the factory's defaults — holding every tool the catalog has.

**The default roster lives in code, never in `appsettings.json`.** Configuration arrays merge by
index, so a roster shipped in that file would be merged *into* an operator's own entries rather than
replaced by them; an operator naming one agent would silently inherit two of ours.

**`UseDefaultRoster` is off by default until the roster's cost has been measured.** A read question
through it costs at least two model round-trips and the specialist sees no history. Whether the
coordinator should hold the read tools itself, or the single agent stays the default and the roster
the opt-in, is decided with numbers once a turn can be run (plan §5.2 item 6); until then the switch
exists and its default is the unmeasured side's opposite.

**An entry inherits the root provider for every field it does not declare, and never its role.**
`Provider` on an entry is a set of nullable overrides — type, endpoint, key, deployment, temperature,
max tokens — each of which, unset, is the root's. Name, description and prompt are not inherited,
because the role is what makes it a different agent; an entry without a description is refused rather
than given the root's.

**The roster is validated whole at startup, and every rule is a startup failure.** Nothing bounds
delegation at runtime — a cycle would recurse until the stack ended — so the graph is checked once,
before the server listens, through the same startup filter that orders the database bootstrap. The
validation needs no provider, so a keyless host still boots. Refused, each with a message naming what
was wrong:

- a repeated or blank name, or an entry without a description;
- a tool name the catalog does not hold, with the names it does;
- a delegate that is not in the roster, or an agent delegating to itself;
- a delegation cycle, named as the path (`a -> b -> a`);
- a `Workflow:Coordinator` that is not in the roster, or several agents with no coordinator named.
  **A roster of one routes to it; several agents and no coordinator is an error, never "the first
  one".**

### The catalog, the registry and the route

**`AgentToolCatalog` holds every tool the application can grant, by name, and narrows it to an
allowlist** in the order the allowlist names them. A name it does not hold throws — an agent silently
missing a capability its entry asked for would look like an agent that chose not to use it — and two
tools with one name are refused at construction.

**`AgentRegistry` builds one handle per roster entry, over its own client from its effective
provider**, holding exactly the tools its entry allows plus **one delegation tool per target**. A
delegation tool is named `delegate_to_<target>` (the target's name with anything a tool name cannot
carry replaced by an underscore) and described by the target's description, so what the model reads is
what the target says it does, written once. Calling it runs the target agent on the request alone — no
session, nothing of the caller's conversation — and returns the target's text. The target is looked up
when the tool runs, so roster order does not matter. Building the registry builds every client, so a
configuration that cannot name a provider fails here, when the registry is first asked for, with the
sentence [SPEC-140](SPEC-140-provider-adapters.md) specifies; a registry that fails half built disposes
the clients it made. Disposing the registry disposes every handle.

**`StaticWorkflowRoute` is where a turn goes: the roster's coordinator, and nothing else decides.** The
roster was checked whole at startup, so there is no per-turn decision left, and a class that made one
would be a second place routing could go wrong. Other agents are reached only through the delegation
tools the model chooses to call.

### The agent factory

One agent is built from a role by `AgentFactory`, over the chat client the provider factory made
([SPEC-140](SPEC-140-provider-adapters.md)) — the root configuration's role, or a roster entry's. It
is the agent framework's chat-client agent, and four things about it are decided here:

- **The role is the definition's.** The name and description are the agent's, with `Folder Assistant`
  and *answers questions about the contents of one folder on this machine* where the root sets
  neither. The instructions are the system prompt **verbatim** when one is set — nothing is appended
  to a prompt the operator wrote — and otherwise a prompt built from the name and description that
  tells the agent to answer from the folder through its tools, to say when they do not hold the
  answer, never to present what it did not find as if it came from the folder, and what a
  `TOOL_FAILED:` result means.
- **The tools are what it may call, and none means none.** The factory takes the tool list and hands
  it to the agent's chat options; an empty list leaves the options without tools rather than with an
  empty set. **Two tools with one name are refused** at construction: a model told of both could not
  say which it meant.
- **The loop is the factory's.** See above: built over the client with no tolerance for a failed
  iteration.
- **The handle owns the client, and says what the agent holds.** `AgentHandle` holds the agent and the
  client it talks through and disposes the client with itself; the framework's agent does not own its
  client, and a client is a disposable pipeline something has to end. Only synchronously disposable
  clients exist, so there is no async half. It also carries the tool list the agent was built with, so
  what an agent may call can be read without running a turn.

The composition root registers the catalog, the roster, the registry and the route as factories. The
roster is resolved at startup by the filter; the registry and the route are resolved by nothing yet,
so a host boots with a provider it cannot reach and a configuration that cannot name one fails when
the registry is first asked for — never at boot.

## Contracts

```csharp
internal record WorkflowConfig            // FolderAssistant:Workflow
{
	Boolean UseDefaultRoster { get; init; }          // false until measured
	String? Coordinator { get; init; }
	List<AgentEntryConfig> Agents { get; init; }
}

internal record AgentEntryConfig
{
	String Name; String? Description; String? SystemPrompt;
	List<String> Tools; List<String> Delegates; ProviderOverrideConfig? Provider;
}

internal record ProviderOverrideConfig     // every field nullable; Apply(root) fills the unset ones
{
	ProviderConfig Apply(ProviderConfig root);
}

internal sealed record AgentDefinition(String Name, String Description, String? SystemPrompt,
	IReadOnlyList<String> Tools, IReadOnlyList<String> Delegates, ProviderConfig Provider);

internal sealed class Roster
{
	static Roster Build(AgentConfig config, IReadOnlyCollection<String> toolNames);   // throws on every rule above
	IReadOnlyList<AgentDefinition> Agents { get; }
	String Coordinator { get; }
	AgentDefinition this[String name] { get; }
}

internal sealed class AgentToolCatalog
{
	AgentToolCatalog(IReadOnlyList<AITool> tools);
	IReadOnlyList<String> Names { get; }
	IReadOnlyList<AITool> Select(IReadOnlyList<String> names);   // throws on an unknown name
}

internal sealed class AgentRegistry : IDisposable
{
	AgentRegistry(Roster roster, AgentToolCatalog catalog, Func<ProviderConfig, IChatClient> clients, ILoggerFactory? loggerFactory = null);
	IReadOnlyList<AgentHandle> Handles { get; }
	AgentHandle Get(String name);
	static String DelegationToolName(String target);
}

internal sealed class StaticWorkflowRoute
{
	StaticWorkflowRoute(Roster roster, AgentRegistry registry);
	AgentHandle Coordinator { get; }
}

internal static class ToolReflection
{
	static IReadOnlyList<AIFunction> Reflect(Object holder);   // throws when nothing is described
}

internal enum ToolGroup { File, Search, Delegation }

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
	static AgentHandle Create(String name, String description, String? systemPrompt, IChatClient client, IReadOnlyList<AITool> tools, ILoggerFactory? loggerFactory = null);
	static IChatClient WithFunctionInvocation(IChatClient client, ILoggerFactory? loggerFactory);
	static String Instructions(String? systemPrompt, String name, String description);
}

internal sealed class AgentHandle : IDisposable
{
	String Name { get; }
	AIAgent Agent { get; }
	IReadOnlyList<AITool> Tools { get; }
}
```

## Non-functional requirements

- Reliability: a roster that cannot run is refused before the server listens; nothing bounds
  delegation at runtime, so nothing at runtime needs to.
- Performance:
- Operability: every tool call, delegations included, is one structured log line naming the tool, its
  group, how it ended and how long it took; a file failure at warning, a search or delegation failure
  at error, a cancellation and a success at information.

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
- **The roster** — nothing configured yields one agent from the root holding every tool; the default
  roster is the three agents with the stated allowlists and the orchestrator holding none; the switch
  is off by default; configured agents replace the default whole; a configured coordinator must exist,
  a roster of one routes to it, several without one are refused; a repeated name, a blank name and a
  missing description are refused; an unknown tool is refused naming the tools that exist;
  self-delegation and an unknown delegate are refused; a two-cycle and a three-cycle are refused
  naming the path, and a diamond is not; **one case per provider field** asserts the declared value is
  the agent's own and every other field the root's; the role is never inherited.
- **The catalog** — the named tools in the order named, none for none; an unknown name and a repeated
  tool refused.
- **The registry and the route** — one handle per entry holding its allowlist and one delegation tool
  per target, named and described by the target, under the delegation group; a delegation through a
  real agent runs the target on the request alone with the target's own prompt and tools and returns
  its text as the function result; a delegate's fatal failure ends the caller's turn on the same
  exception with neither model asked again; disposing the registry disposes every client, and a build
  that fails on the third client has disposed the first two; the delegation tool name is made safe.
- **Host** — asked for, the default roster resolves from the real root as three handles with the
  orchestrator holding nothing but its two delegations and the route entering it; a configured roster
  whose delegation forms a cycle stops the host before it listens; the single agent still resolves
  through the route with all ten tools, and a keyless configuration fails when the registry is asked
  for, not at boot.
- Mutation kills, each restored byte-for-byte: the loop left at its default fails exactly the
  turn-ending test; the facade handling both groups as strings fails the search contract and the
  turn-ending test; the token check inverted fails the three cancellation tests; the description
  filter removed from the reflection fails the two tests that name it. A type-only classification
  could not be written as a runtime mutant — the analyzer refuses the always-true clause — so the
  inverted check is the evidence that the token, not the type, decides. For the roster: self-delegation
  allowed fails exactly its test; the first agent made the coordinator fails exactly the coordinator
  test; a delegation put under the file contract fails the two registry tests that name the group; the
  catalog ignoring an unknown name fails exactly its test; the temperature no longer inherited fails
  the five inheritance cases that leave it undeclared — a first version of that theory had masked the
  field it was checking, which the surviving mutant exposed; the cycle detection turned into a return
  fails the roster's cycle test and the host's boot test, and turned into a never-true check it ends
  the test host in a stack overflow, which is the recursion the check exists to stop.

Not covered: turn execution and session persistence, because they are not built yet.

## Open questions

- **Whether the default roster becomes the default** waits on the measurement above; the switch and
  both rosters exist so that the measurement is one configuration value away once a turn can run.

## References

- [System concept](SPEC-000-system-concept.md)
- [SPEC-101 — File tools](SPEC-101-file-tools.md)
- [SPEC-140 — Provider adapters](SPEC-140-provider-adapters.md)

## Changelog

- **0.5.0** (2026-09-18) — the roster, the catalog, the registry with one delegation tool per target
  under a third, fatal group, and the static route; three rosters by precedence with the default in
  code and off until measured; per-field provider inheritance; every roster rule a startup failure
  through the startup filter; the factory taking a role as well as the root configuration. Written
  with their implementation.
- **0.4.0** (2026-09-16) — the tool reflection, the facade with its two contracts, the loop built to
  tolerate no failed iteration, the duplicate-name refusal, the failure sentence in the built prompt,
  and the handle's tool list; written with their implementation.
- **0.3.0** (2026-09-16) — the agent factory and the handle, written with their implementation.
- **0.2.0** (2026-09-09) — the composition root's rules: lazy binding, startup order.
