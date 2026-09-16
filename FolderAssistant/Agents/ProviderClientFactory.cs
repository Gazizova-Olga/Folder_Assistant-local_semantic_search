using System.ClientModel;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace FolderAssistant.Agents;

/// <summary>
/// Builds the chat client the agent talks through, from the provider configuration (SPEC-140). One
/// provider family, in two shapes: an OpenAI-compatible endpoint — the hosted OpenAI API, or any server
/// speaking its protocol, a local Ollama included — and Azure OpenAI, which is the same client with a
/// resource endpoint and its own authentication.
///
/// <para>
/// This is the one place the application can be pointed at a hosted model, and so the one place
/// document text can leave the machine (SPEC-000). Nothing here reaches the network at construction:
/// a client is built, not connected, so a host boots with a provider it cannot reach and fails on the
/// first turn instead of at startup.
/// </para>
///
/// <para>
/// Two things are decided at construction rather than discovered on the first call. A configuration
/// that cannot name a reachable provider — Azure without an endpoint or a key, the hosted OpenAI API
/// without a key — throws with a sentence saying what is missing, because a client that would fail
/// every call with a transport error is a silently plausible failure a page later. And the client
/// library's own network timeout is set to the configured connection timeout: the SDK defaults it to
/// 100 seconds independently of anything the host configures, and a turn that hangs for that long
/// reads as a dead application.
/// </para>
/// </summary>
internal static class ProviderClientFactory
{
	/// <summary>The credential handed to a compatible server that authenticates nothing; the SDK refuses an empty one.</summary>
	internal const String NoKeyPlaceholder = "no-key";

	/// <summary>
	/// The chat client for the configured provider, with the sampling options applied through the
	/// abstraction's own builder so they hold for every call whichever agent makes it.
	/// </summary>
	public static IChatClient Create(ProviderConfig provider, TimeSpan connectionTimeout)
	{
		ArgumentNullException.ThrowIfNull(provider);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(connectionTimeout, TimeSpan.Zero);

		if (String.IsNullOrWhiteSpace(provider.DeploymentName))
		{
			throw new InvalidOperationException("Provider:DeploymentName must name the model or deployment to talk to.");
		}

		IChatClient raw = provider.Type switch
		{
			ProviderConfig.AiProviderType.Azure => CreateAzure(provider, connectionTimeout),
			ProviderConfig.AiProviderType.OpenAI => CreateOpenAI(provider, connectionTimeout),
			_ => throw new InvalidOperationException($"Provider:Type '{provider.Type}' is not a provider this application knows."),
		};

		return WithDefaults(raw, provider);
	}

	/// <summary>
	/// The configured sampling options as defaults on every call through <paramref name="raw"/>: an
	/// option a call sets for itself is kept, an unset one takes the configured value. Applied through
	/// the abstraction's builder so it holds whichever agent makes the call; separate so a test can
	/// read the values back through a recording client.
	/// </summary>
	internal static IChatClient WithDefaults(IChatClient raw, ProviderConfig provider)
	{
		ArgumentNullException.ThrowIfNull(raw);
		ArgumentNullException.ThrowIfNull(provider);

		return new ChatClientBuilder(raw)
			.ConfigureOptions(options =>
			{
				options.Temperature ??= (Single?)provider.Temperature;
				options.MaxOutputTokens ??= provider.MaxTokens;
			})
			.Build();
	}

	/// <summary>
	/// The pipeline options every client here is built with. Separate and internal so a test can hold
	/// the one property this exists to set — the network timeout — rather than infer it from a hang.
	/// </summary>
	internal static OpenAIClientOptions OpenAIOptions(ProviderConfig provider, TimeSpan connectionTimeout)
	{
		OpenAIClientOptions options = new() { NetworkTimeout = connectionTimeout };
		if (!String.IsNullOrWhiteSpace(provider.Endpoint))
		{
			options.Endpoint = ParseEndpoint(provider.Endpoint);
		}

		return options;
	}

	internal static AzureOpenAIClientOptions AzureOptions(TimeSpan connectionTimeout)
		=> new() { NetworkTimeout = connectionTimeout };

	private static IChatClient CreateOpenAI(ProviderConfig provider, TimeSpan connectionTimeout)
	{
		// A compatible server on a configured endpoint may authenticate nothing — a local Ollama does
		// not — so a missing key is allowed there and a placeholder stands in. The hosted API is the
		// default endpoint, and it refuses every call without a key; that is said here, once, rather
		// than as a 401 on the first turn.
		Boolean hasEndpoint = !String.IsNullOrWhiteSpace(provider.Endpoint);
		Boolean hasKey = !String.IsNullOrWhiteSpace(provider.ApiKey);
		if (!hasEndpoint && !hasKey)
		{
			throw new InvalidOperationException(
				"Provider:ApiKey is required for the hosted OpenAI API. Set it through the environment variable "
				+ "FolderAssistant__Provider__ApiKey or user secrets, or set Provider:Endpoint to a local "
				+ "OpenAI-compatible server such as http://localhost:11434/v1, which needs no key.");
		}

		OpenAIClient client = new(new ApiKeyCredential(hasKey ? provider.ApiKey! : NoKeyPlaceholder), OpenAIOptions(provider, connectionTimeout));

		return client.GetChatClient(provider.DeploymentName).AsIChatClient();
	}

	private static IChatClient CreateAzure(ProviderConfig provider, TimeSpan connectionTimeout)
	{
		if (String.IsNullOrWhiteSpace(provider.Endpoint))
		{
			throw new InvalidOperationException("Provider:Endpoint is required for Azure OpenAI: the resource URL, such as https://<resource>.openai.azure.com/.");
		}

		if (String.IsNullOrWhiteSpace(provider.ApiKey))
		{
			throw new InvalidOperationException("Provider:ApiKey is required for Azure OpenAI. Set it through the environment variable FolderAssistant__Provider__ApiKey or user secrets.");
		}

		AzureOpenAIClient client = new(ParseEndpoint(provider.Endpoint), new ApiKeyCredential(provider.ApiKey), AzureOptions(connectionTimeout));

		return client.GetChatClient(provider.DeploymentName).AsIChatClient();
	}

	private static Uri ParseEndpoint(String endpoint)
	{
		if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
		{
			throw new InvalidOperationException($"Provider:Endpoint '{endpoint}' is not an absolute http or https URL.");
		}

		return uri;
	}
}
