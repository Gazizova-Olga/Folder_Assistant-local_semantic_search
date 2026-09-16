# SPEC-140-provider-adapters — Provider Adapters

| | |
|---|---|
| Status | Draft — client construction written and implemented; the readable failure contract is not |
| Version | 0.2.0 |
| Owner | Agents |
| Last updated | 2026-09-16 |

## Purpose

Presents one invocation surface over the external language-model providers the agent can be
configured against.

## Scope

**In scope**

- Provider selection and client construction.
- Timeout and option mapping.
- A uniform failure contract (not yet written: a provider failure surfaces as the SDK's exception
  until the describer that turns one into a sentence exists).

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

## Contracts

```csharp
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

## Open questions

- The readable failure contract: a `ProviderErrorDescriber` turning transport and status failures
  into one actionable sentence, `Retry-After` included. Not built.
- Whether a local endpoint should be required to be loopback unless an explicit opt-out is set, the
  same question [SPEC-162](SPEC-162-embedding-ollama-local.md) leaves open for the embedder.

## References

- [System concept](SPEC-000-system-concept.md)
- [SPEC-100 — Conversation orchestration](SPEC-100-conversation-orchestration.md)

## Changelog

- **0.2.0** (2026-09-16) — client construction written with its implementation: the two shapes,
  what is refused at construction, the network timeout, the sampling defaults, the trust boundary.
- **0.1.0** (2026-09-08) — placeholder.
