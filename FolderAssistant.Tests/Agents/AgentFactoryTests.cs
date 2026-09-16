using FluentAssertions;
using FolderAssistant.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FolderAssistant.Tests.Agents;

/// <summary>
/// The agent factory (SPEC-100): the role comes from the configuration, the prompt is the configured one
/// verbatim or one built from the name and description, the tools reach the model, and the handle owns
/// the client — each asserted through a recording client rather than a model.
/// </summary>
public sealed class AgentFactoryTests
{
	[Fact]
	public async Task The_Configured_Prompt_Is_Sent_Verbatim_And_The_Name_Is_The_Handles()
	{
		AgentConfig config = new() { AgentName = "Archivist", AgentDescription = "keeps the notes", SystemPrompt = "Only the prompt." };
		RecordingChatClient client = new();

		using AgentHandle handle = AgentFactory.Create(config, client, tools: []);
		AgentResponse response = await handle.Agent.RunAsync("hello");

		handle.Name.Should().Be("Archivist");
		handle.Agent.Name.Should().Be("Archivist");
		handle.Agent.Description.Should().Be("keeps the notes");
		response.Text.Should().Be("recorded reply");
		client.Calls.Should().ContainSingle();
		client.Calls[0].Options!.Instructions.Should().Be("Only the prompt.");
	}

	[Fact]
	public void Without_A_Prompt_One_Is_Built_From_The_Name_And_Description_Or_Their_Defaults()
	{
		AgentConfig named = new() { AgentName = "Archivist", AgentDescription = "keeps the notes" };
		AgentConfig bare = new();

		String fromNamed = AgentFactory.Instructions(named, "Archivist", "keeps the notes");
		String fromBare = AgentFactory.Instructions(bare, AgentFactory.DefaultName, AgentFactory.DefaultDescription);

		fromNamed.Should().StartWith("You are Archivist, an assistant that keeps the notes.");
		fromBare.Should().StartWith($"You are {AgentFactory.DefaultName}, an assistant that {AgentFactory.DefaultDescription}.");
		fromBare.Should().Contain("Never present something you did not find in the folder");
		AgentFactory.Create(bare, new RecordingChatClient(), tools: []).Name.Should().Be(AgentFactory.DefaultName);
	}

	[Fact]
	public async Task The_Tools_Reach_The_Model_And_None_Means_None()
	{
		AITool tool = AIFunctionFactory.Create(() => "pong", "Ping", "Answers pong.");
		RecordingChatClient withTools = new();
		RecordingChatClient without = new();

		using AgentHandle armed = AgentFactory.Create(new AgentConfig(), withTools, tools: [tool]);
		using AgentHandle bare = AgentFactory.Create(new AgentConfig(), without, tools: []);
		await armed.Agent.RunAsync("hi");
		await bare.Agent.RunAsync("hi");

		withTools.Calls[0].Options!.Tools.Should().ContainSingle(t => t.Name == "Ping");
		(without.Calls[0].Options?.Tools?.Count ?? 0).Should().Be(0);
	}

	[Fact]
	public void Disposing_The_Handle_Disposes_The_Client()
	{
		RecordingChatClient client = new();

		AgentHandle handle = AgentFactory.Create(new AgentConfig(), client, tools: []);
		handle.Dispose();

		client.Disposed.Should().BeTrue();
	}

	/// <summary>
	/// The sampling options ride on the client, not the agent, so they hold for every caller of that
	/// client. Read back through the factory's builder over a recording client.
	/// </summary>
	[Fact]
	public async Task Sampling_Options_From_The_Configuration_Reach_Every_Call()
	{
		RecordingChatClient inner = new();
		IChatClient configured = ProviderClientFactory.WithDefaults(inner, new ProviderConfig { Temperature = 0.2, MaxTokens = 512 });

		using AgentHandle handle = AgentFactory.Create(new AgentConfig(), configured, tools: []);
		await handle.Agent.RunAsync("hi");

		inner.Calls[0].Options!.Temperature.Should().Be(0.2f);
		inner.Calls[0].Options!.MaxOutputTokens.Should().Be(512);
	}
}
