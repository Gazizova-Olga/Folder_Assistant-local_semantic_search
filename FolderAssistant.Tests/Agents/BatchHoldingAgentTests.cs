using FluentAssertions;
using FolderAssistant.Agents;
using FolderAssistant.Tests.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderAssistant.Tests.Agents;

/// <summary>
/// The index's batch held for the whole of an agent run (SPEC-100, SPEC-121), through a real agent over
/// a scripted client whose tool looks at the hold while it runs: one hold per run, open while a tool
/// executes and released when the run ends, however it ends; a streamed run held until the enumeration
/// is disposed; a delegate's run nested inside its caller's; and none of it without the indexer.
/// </summary>
public sealed class BatchHoldingAgentTests
{
	[Fact]
	public async Task A_Run_Holds_Once_From_Before_Its_First_Tool_Call_To_After_Its_Answer()
	{
		RecordingNotifier indexer = new();
		Int32 holdsDuringTool = -1;
		AITool tool = new ToolFacade(AIFunctionFactory.Create(() => { holdsDuringTool = indexer.OpenHolds; return "written"; }, "Write"), ToolGroup.File, NullLogger.Instance);
		using AgentHandle handle = Handle(new ScriptedChatClient(ScriptedChatClient.Call("Write", []), ScriptedChatClient.Text("done")), [tool], indexer);

		AgentResponse response = await handle.Agent.RunAsync("write it");

		response.Text.Should().Be("done");
		holdsDuringTool.Should().Be(1);
		indexer.HoldsBegun.Should().Be(1);
		indexer.OpenHolds.Should().Be(0);
	}

	[Fact]
	public async Task A_Run_That_Fails_Releases_Its_Hold_On_The_Way_Out()
	{
		RecordingNotifier indexer = new();
		using AgentHandle handle = Handle(new FaultingClient(new InvalidOperationException("no")), [], indexer);

		Func<Task> run = () => handle.Agent.RunAsync("anything");

		await run.Should().ThrowAsync<InvalidOperationException>();
		indexer.HoldsBegun.Should().Be(1);
		indexer.OpenHolds.Should().Be(0);
	}

	[Fact]
	public async Task A_Streamed_Run_Holds_Until_The_Enumeration_Is_Disposed()
	{
		RecordingNotifier indexer = new();
		using AgentHandle handle = Handle(new ScriptedChatClient(ScriptedChatClient.Text("a long answer")), [], indexer);

		IAsyncEnumerator<AgentResponseUpdate> updates = handle.Agent.RunStreamingAsync("go").GetAsyncEnumerator();
		indexer.OpenHolds.Should().Be(0, "nothing runs until the enumeration starts");

		(await updates.MoveNextAsync()).Should().BeTrue();
		indexer.OpenHolds.Should().Be(1);

		await updates.DisposeAsync();
		indexer.OpenHolds.Should().Be(0);
		indexer.HoldsBegun.Should().Be(1);
	}

	[Fact]
	public async Task A_Delegates_Run_Holds_Inside_Its_Callers_And_Both_Are_Released()
	{
		RecordingNotifier indexer = new();
		Int32 holdsDuringDelegateTool = -1;
		AITool tool = new ToolFacade(AIFunctionFactory.Create(() => { holdsDuringDelegateTool = indexer.OpenHolds; return "ok"; }, "Read"), ToolGroup.File, NullLogger.Instance);
		Roster roster = Roster.Build(
			new AgentConfig
			{
				Workflow = new WorkflowConfig
				{
					Coordinator = "boss",
					Agents =
					[
						new AgentEntryConfig { Name = "boss", Description = "the boss", Delegates = ["reader"], Provider = new ProviderOverrideConfig { DeploymentName = "boss" } },
						new AgentEntryConfig { Name = "reader", Description = "the reader", Tools = ["Read"], Provider = new ProviderOverrideConfig { DeploymentName = "reader" } },
					],
				},
			},
			["Read"]);
		Dictionary<String, IChatClient> clients = new()
		{
			["boss"] = new ScriptedChatClient(ScriptedChatClient.Call("delegate_to_reader", new() { ["request"] = "read it" }), ScriptedChatClient.Text("done")),
			["reader"] = new ScriptedChatClient(ScriptedChatClient.Call("Read", []), ScriptedChatClient.Text("read")),
		};
		using AgentRegistry registry = new(roster, new AgentToolCatalog([tool]), provider => clients[provider.DeploymentName], indexer: indexer);

		await registry.Get("boss").Agent.RunAsync("go");

		holdsDuringDelegateTool.Should().Be(2);
		indexer.DeepestHold.Should().Be(2);
		indexer.HoldsBegun.Should().Be(2);
		indexer.OpenHolds.Should().Be(0);
	}

	[Fact]
	public async Task Without_An_Indexer_The_Agent_Runs_Bare()
	{
		using AgentHandle handle = AgentFactory.Create("solo", "the solo", null, new ScriptedChatClient(ScriptedChatClient.Text("bare")), []);

		handle.Agent.Should().BeOfType<ChatClientAgent>();
		(await handle.Agent.RunAsync("go")).Text.Should().Be("bare");
	}

	private static AgentHandle Handle(IChatClient client, IReadOnlyList<AITool> tools, RecordingNotifier indexer)
		=> AgentFactory.Create("solo", "the solo", null, client, tools, indexer: indexer);

	private sealed class FaultingClient(Exception fault) : IChatClient
	{
		public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
			=> Task.FromException<ChatResponse>(fault);

		public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
			=> throw fault;

		public Object? GetService(Type serviceType, Object? serviceKey = null) => null;

		public void Dispose()
		{
			// Holds nothing.
		}
	}
}
