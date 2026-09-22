# SPEC-140-provider-adapters — Provider Adapters

| | |
|---|---|
| Status | Draft — client construction and the readable failure contract written and implemented |
| Version | 0.3.0 |
| Owner | Agents |
| Last updated | 2026-09-23 |

## Purpose

Presents one invocation surface over the external language-model providers the agent can be
configured against.

## Scope

**In scope**

- Provider selection and client construction.
- Timeout and option mapping.
- A uniform failure contract: a provider failure surfaces as the SDK's exception, and one describer
  turns it into a sentence for whoever has to show it.

**Out of scope**

- Agent behaviour and routing ([SPEC-100](SPEC-100-conversation-orchestration.md)).
- Embedding providers ([SPEC-160](SPEC-160-embedding-module.md)); the embedding client to a local
  Ollama is that spec's, and is never a hosted endpoint.

## Requirements

### One family, two shapes

The chat client is built from `Provider` configuration by one factory, `ProviderClientFactory`, in
one of two shapes of the same client family:

- **`OpenAI`** — an OpenAI-compatible endpoint. With no `Endpoint` it is the hosted OpenAI API;
  with one, any server speaking that protocol, a local Ollama's `http://localhost:11434/v1`
  included.
- **`Azure`** — Azure OpenAI: the same client over a resource endpoint with its own
  authentication.

The result is the abstraction's `IChatClient`, so nothing above the factory knows which shape it
got. `DeploymentName` is the model or deployment either shape targets, and is required.

### What is refused at construction, and what is not

A client is **built, not connected**. Nothing here reaches the network, so a host boots with a
provider it cannot reach and the first turn fails instead of startup — the index has to come up
whether or not a model is reachable. What *is* decided at construction is whether the configuration
can name a provider at all, because a client that would fail every call with a transport error is a
silently plausible failure a page later:

- `Azure` without an `Endpoint`, or without an `ApiKey`, throws naming the missing key.
- `OpenAI` with neither an `Endpoint` nor an `ApiKey` throws: the hosted API refuses every call
  without a key, and the sentence says where to put one and that a local endpoint needs none.
- `OpenAI` with an `Endpoint` and no key builds, with a placeholder credential, because a compatible
  local server authenticates nothing and the SDK refuses an empty credential.
- An `Endpoint` that is not an absolute `http` or `https` URL, an empty `DeploymentName`, and a
  connection timeout of zero or less each throw.

Every message names the configuration key (`Provider:ApiKey`, `Provider:Endpoint`) and, for the key,
the environment variable `FolderAssistant__Provider__ApiKey` that supplies it without a file.

### The network timeout is the configured one

The client library keeps a network timeout of its own, **100 seconds by default, independent of
anything the host configures**. Both option shapes are built with `NetworkTimeout` set to the
configured `ConnectionTimeoutSeconds`, so a turn against a server that accepts and never answers is
given up on, retries included, in a few seconds rather than reading as a dead application. The
SDK's retry policy is left as it is; the timeout bounds each attempt.

### Sampling options ride on the client

`Temperature` and `MaxTokens`, where configured, are applied through the abstraction's
`ChatClientBuilder` as defaults that fill an unset option on every call, whichever agent makes it. A
call that sets its own keeps its own.

### A failure is one sentence that says what to do

**A provider's failure reaches the model's caller as the SDK's exception, and `ProviderErrorDescriber`
turns it into one sentence.** The SDK's own message is a status line, a wire-format error body and a
chain of inner exceptions; what a person at a console or a browser needs is which configuration key
to look at, or when to try again. The describer answers for what it recognises, walking the inner
chain for the first thing it does — the SDK wraps a refused connection two exceptions deep — and
**answers null for anything else**, so a failure that is not the provider's (the index's refusal, a
tool's own fault) keeps its own message rather than being misfiled as one.

| Failure | The sentence names |
|---|---|
| HTTP 401, 403 | `Provider:ApiKey` and `FolderAssistant__Provider__ApiKey` |
| HTTP 404 | `Provider:DeploymentName`, and `Provider:Endpoint` for a local server |
| HTTP 429 | the rate limit, and `Retry-After` when the server sent one |
| HTTP 5xx | the server's own failure, the reason phrase, `Retry-After` when sent, and that nothing here is misconfigured |
| other 4xx | the request was rejected: `Provider:DeploymentName` and the sampling options |
| a transport that could not connect | the transport's message, `Provider:Endpoint`, and that the server must be running |
| a socket error | the socket error's name, and the same |
| a timeout, or a cancellation that reached the describer | `ConnectionTimeoutSeconds` — the describer is not told about the caller's token, so a cancellation the caller asked for is never handed to it (SPEC-100 classifies first) |

**`Retry-After` is said as an HTTP date whichever form the server sent it in.** A delta in seconds is
added to the describer's clock — a `TimeProvider`, so a test can pin the date — and formatted as
RFC 1123; an HTTP date is passed through in that form; anything else is passed through as it came. A
person reading "try again after Wed, 23 Sep 2026 21:14:05 GMT" can act on it; "after 120" can only be
counted from a moment they did not see.

**Who uses it:** the turn (SPEC-100) describes the failure of a streamed turn in its last text update,
falling back to the exception's own message; the front ends that show a thrown turn's failure use it the
same way when they exist. The log line keeps the exception.

## Contracts

```csharp
internal sealed class ProviderErrorDescriber
{
	ProviderErrorDescriber(TimeProvider? clock = null);
	String? Describe(Exception exception);          // null when nothing in the chain is the provider's
	String DescribeOrMessage(Exception exception);
}

internal static class ProviderClientFactory
{
	static IChatClient Create(ProviderConfig provider, TimeSpan connectionTimeout);   // throws InvalidOperationException
	static OpenAIClientOptions OpenAIOptions(ProviderConfig provider, TimeSpan connectionTimeout);
	static AzureOpenAIClientOptions AzureOptions(TimeSpan connectionTimeout);
	static IChatClient WithDefaults(IChatClient raw, ProviderConfig provider);   // the sampling defaults layer
}
```

Configuration, under `FolderAssistant:Provider`: `Type` (`OpenAI`, the shipped default in
`appsettings.json`; `Azure`), `Endpoint`, `ApiKey`, `DeploymentName`, `Temperature`, `MaxTokens`;
and `FolderAssistant:ConnectionTimeoutSeconds` (30).

## Trust boundary

This factory is the one place the application can be pointed at a hosted model, and so the one place
document text can leave the machine. [SPEC-000](SPEC-000-system-concept.md)'s rule that no profile
may reach the network stands for embedding; the chat provider is the deliberate exception, chosen by
the operator in configuration and by nothing else. The shipped configuration names the hosted OpenAI
shape with no key, which builds nothing until one is supplied or the endpoint is pointed at a local
server.

## Non-functional requirements

- Reliability: a configuration that cannot name a provider fails when the client is first asked for,
  with a sentence, never at boot and never on the first turn.
- Performance: no call is made at construction; a hung server costs the configured timeout per
  attempt, not the SDK's hundred seconds.
- Operability: every refusal names the configuration key and the environment variable that supplies
  it.

## Test strategy

- A compatible endpoint builds without a key and reports, through the client's metadata, the
  endpoint and model it was told; the hosted API without a key is refused with the sentence naming
  the variable and the local alternative, and builds with one; Azure without an endpoint and without
  a key is refused naming each, and builds with both, reporting the endpoint; a bad endpoint, an
  empty deployment and a zero timeout throw.
- The network timeout: both option shapes carry the configured value, asserted directly; and,
  observed, a listener that accepts and never answers is given up on well inside the SDK's default.
- The sampling defaults reach every call, read back through a recording client under the agent.
- A host test resolves the agent handle from the real root over a configured local endpoint with no
  network, and asserts that a keyless hosted configuration fails when the handle is asked for, not
  at boot.
- **The describer** — each status row above names its key; the reason phrase is carried; `Retry-After`
  in seconds, as an HTTP date, and as neither each come out as the table says, and a 5xx carries it
  too; a transport with no status points at the endpoint and one with a status reports it; a timeout
  and a cancellation give the same sentence; a result exception with no status over a request exception
  over a socket error is described from the request exception, and a socket error under a foreign
  exception from the socket; the index's refusal and a plain fault are null, and `DescribeOrMessage`
  gives their own message. In SPEC-100's suite, a streamed turn ending on a cancellation nobody asked
  for carries the describer's sentence and not the SDK's message.
- Mutation kills, each restored byte-for-byte: the delta counted from the wrong moment fails the
  seconds row and the 5xx test; the inner chain not walked fails the chain test; the unrecognised
  described as its own message fails the null test and the chain test; the execution handing the
  describer an exception it never recognises fails SPEC-100's streamed cancellation test. Two
  first attempts (the delta branch disabled, the describer field unread) only failed the analyzer and
  were discarded as non-evidence.

## Open questions

- Whether a local endpoint should be required to be loopback unless an explicit opt-out is set, the
  same question [SPEC-162](SPEC-162-embedding-ollama-local.md) leaves open for the embedder.

## References

- [System concept](SPEC-000-system-concept.md)
- [SPEC-100 — Conversation orchestration](SPEC-100-conversation-orchestration.md)

## Changelog

- **0.3.0** (2026-09-23) — the readable failure contract: one describer, one sentence per kind of
  provider failure naming the key or the time, `Retry-After` as an HTTP date, null for what is not
  the provider's; used by the turn's streamed failure note. Written with its implementation.
- **0.2.0** (2026-09-16) — client construction written with its implementation: the two shapes,
  what is refused at construction, the network timeout, the sampling defaults, the trust boundary.
- **0.1.0** (2026-09-08) — placeholder.
