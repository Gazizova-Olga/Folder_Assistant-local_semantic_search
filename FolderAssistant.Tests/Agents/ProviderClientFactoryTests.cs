using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using FolderAssistant.Agents;
using Microsoft.Extensions.AI;

namespace FolderAssistant.Tests.Agents;

/// <summary>
/// The provider factory (SPEC-140): which configurations build a client and which are refused at
/// construction with a sentence, what the client reports about where it points, and the one pipeline
/// property this factory exists to set — a network timeout that is the configured one, not the SDK's.
/// </summary>
public sealed class ProviderClientFactoryTests
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(7);

	[Fact]
	public void A_Compatible_Endpoint_Builds_Without_A_Key_And_Points_Where_It_Was_Told()
	{
		ProviderConfig provider = new()
		{
			Type = ProviderConfig.AiProviderType.OpenAI,
			Endpoint = "http://localhost:11434/v1",
			DeploymentName = "qwen3:0.6b",
		};

		using IChatClient client = ProviderClientFactory.Create(provider, Timeout);
		ChatClientMetadata? metadata = client.GetService<ChatClientMetadata>();

		metadata.Should().NotBeNull();
		metadata!.ProviderUri.Should().Be(new Uri("http://localhost:11434/v1"));
		metadata.DefaultModelId.Should().Be("qwen3:0.6b");
	}

	[Fact]
	public void The_Hosted_OpenAI_Api_Needs_A_Key_And_Says_So()
	{
		ProviderConfig provider = new() { Type = ProviderConfig.AiProviderType.OpenAI, DeploymentName = "gpt-chat" };

		Action act = () => ProviderClientFactory.Create(provider, Timeout);

		act.Should().Throw<InvalidOperationException>().WithMessage("*Provider:ApiKey*FolderAssistant__Provider__ApiKey*localhost:11434*");
	}

	[Fact]
	public void The_Hosted_OpenAI_Api_Builds_With_A_Key()
	{
		ProviderConfig provider = new() { Type = ProviderConfig.AiProviderType.OpenAI, ApiKey = "sk-test", DeploymentName = "gpt-chat" };

		using IChatClient client = ProviderClientFactory.Create(provider, Timeout);

		client.GetService<ChatClientMetadata>()!.DefaultModelId.Should().Be("gpt-chat");
	}

	[Fact]
	public void Azure_Needs_An_Endpoint_And_A_Key_And_Names_The_Missing_One()
	{
		ProviderConfig noEndpoint = new() { Type = ProviderConfig.AiProviderType.Azure, ApiKey = "k", DeploymentName = "d" };
		ProviderConfig noKey = new() { Type = ProviderConfig.AiProviderType.Azure, Endpoint = "https://r.openai.azure.com/", DeploymentName = "d" };
		ProviderConfig both = new() { Type = ProviderConfig.AiProviderType.Azure, Endpoint = "https://r.openai.azure.com/", ApiKey = "k", DeploymentName = "d" };

		Action missingEndpoint = () => ProviderClientFactory.Create(noEndpoint, Timeout);
		Action missingKey = () => ProviderClientFactory.Create(noKey, Timeout);
		using IChatClient client = ProviderClientFactory.Create(both, Timeout);

		missingEndpoint.Should().Throw<InvalidOperationException>().WithMessage("*Provider:Endpoint*Azure*");
		missingKey.Should().Throw<InvalidOperationException>().WithMessage("*Provider:ApiKey*Azure*");
		client.GetService<ChatClientMetadata>()!.ProviderUri.Should().Be(new Uri("https://r.openai.azure.com/"));
	}

	[Fact]
	public void A_Bad_Endpoint_And_A_Missing_Deployment_Are_Refused()
	{
		ProviderConfig badEndpoint = new() { Type = ProviderConfig.AiProviderType.OpenAI, Endpoint = "not a url", DeploymentName = "d" };
		ProviderConfig noDeployment = new() { Type = ProviderConfig.AiProviderType.OpenAI, Endpoint = "http://localhost:1/v1", DeploymentName = "" };

		Action endpoint = () => ProviderClientFactory.Create(badEndpoint, Timeout);
		Action deployment = () => ProviderClientFactory.Create(noDeployment, Timeout);
		Action zero = () => ProviderClientFactory.Create(badEndpoint, TimeSpan.Zero);

		endpoint.Should().Throw<InvalidOperationException>().WithMessage("*Provider:Endpoint*absolute*");
		deployment.Should().Throw<InvalidOperationException>().WithMessage("*Provider:DeploymentName*");
		zero.Should().Throw<ArgumentOutOfRangeException>();
	}

	/// <summary>
	/// The property the factory exists for. The SDK's own timeout defaults to 100 seconds whatever the
	/// host configures; both option shapes carry the configured one instead.
	/// </summary>
	[Fact]
	public void The_Network_Timeout_Is_The_Configured_Connection_Timeout()
	{
		ProviderConfig provider = new() { Endpoint = "http://localhost:11434/v1" };

		ProviderClientFactory.OpenAIOptions(provider, Timeout).NetworkTimeout.Should().Be(Timeout);
		ProviderClientFactory.OpenAIOptions(provider, Timeout).Endpoint.Should().Be(new Uri("http://localhost:11434/v1"));
		ProviderClientFactory.AzureOptions(Timeout).NetworkTimeout.Should().Be(Timeout);
	}

	/// <summary>
	/// The same property, observed: a server that accepts the connection and never answers. With the
	/// SDK's default the call would sit for 100 seconds before its first retry; with the configured
	/// timeout the whole pipeline, retries included, gives up in a few seconds. The bound asserted is
	/// loose on purpose — retries back off — but far inside the default.
	/// </summary>
	[Fact]
	public async Task A_Server_That_Never_Answers_Is_Given_Up_On_Within_The_Configured_Timeout_Not_The_Sdks()
	{
		using TcpListener listener = new(IPAddress.Loopback, 0);
		listener.Start();
		Int32 port = ((IPEndPoint)listener.LocalEndpoint).Port;
		List<TcpClient> held = [];
		Task accepting = Task.Run(async () =>
		{
			try
			{
				while (true)
				{
					held.Add(await listener.AcceptTcpClientAsync());
				}
			}
			catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
			{
				// The listener was stopped; the test is over.
			}
		});

		ProviderConfig provider = new() { Type = ProviderConfig.AiProviderType.OpenAI, Endpoint = $"http://127.0.0.1:{port}/v1", DeploymentName = "m" };
		TimeSpan timeout = TimeSpan.FromMilliseconds(300);
		using IChatClient client = ProviderClientFactory.Create(provider, timeout);

		Stopwatch clock = Stopwatch.StartNew();
		Func<Task> act = () => client.GetResponseAsync("hello");

		await act.Should().ThrowAsync<Exception>();
		clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20), "the configured timeout bounds every attempt; the SDK's default alone would be 100 s");

		listener.Stop();
		await accepting;
		held.ForEach(static c => c.Dispose());
	}

	/// <summary>
	/// The configured sampling values reach a call that did not set its own, and a call that did keeps
	/// its own — read back through a recording client under the same defaults layer the factory builds.
	/// </summary>
	[Fact]
	public async Task Sampling_Options_Are_Defaults_On_Every_Call_And_Never_Overrides()
	{
		RecordingChatClient inner = new();
		ProviderConfig provider = new() { Temperature = 0.2, MaxTokens = 512 };

		using IChatClient client = ProviderClientFactory.WithDefaults(inner, provider);
		await client.GetResponseAsync("unset");
		await client.GetResponseAsync("set", new ChatOptions { Temperature = 0.9f, MaxOutputTokens = 8 });

		inner.Calls[0].Options!.Temperature.Should().Be(0.2f);
		inner.Calls[0].Options!.MaxOutputTokens.Should().Be(512);
		inner.Calls[1].Options!.Temperature.Should().Be(0.9f);
		inner.Calls[1].Options!.MaxOutputTokens.Should().Be(8);
	}

	[Fact]
	public async Task Unset_Sampling_Options_Leave_A_Call_Untouched()
	{
		RecordingChatClient inner = new();

		using IChatClient client = ProviderClientFactory.WithDefaults(inner, new ProviderConfig());
		await client.GetResponseAsync("unset");

		inner.Calls[0].Options!.Temperature.Should().BeNull();
		inner.Calls[0].Options!.MaxOutputTokens.Should().BeNull();
	}
}
