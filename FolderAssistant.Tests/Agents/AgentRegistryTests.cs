using FluentAssertions;
using FolderAssistant.Agents;
using FolderAssistant.Retrieval;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderAssistant.Tests.Agents;

/// <summary>
/// The registry and the route (SPEC-100): one handle per roster entry over its own client, holding
/// exactly its allowlist plus one delegation tool per target; a delegation that runs the target on the
/// request alone; a delegate's failure ending the caller's turn; and every client ended with the registry.
/// Each agent's client is picked by its deployment name, so a test can hand each agent its own script.
/// </summary>
public sealed class AgentRegistryTests
{
	private static AgentEntryConfig Entry(String name, String[]? tools = null, String[]? delegates = null)
		=> new() { Name = name, Description = $"the {name}", Tools = [.. tools ?? []], Delegates = [.. delegates ?? []], Provider = new ProviderOverrideConfig { DeploymentName = name } };

	private static AgentToolCatalog Catalog(params AITool[] tools) => new(tools);

	private static AITool Tool(String name, String reply = "ok")
		=> new ToolFacade(AIFunctionFactory.Create(() => reply, name), ToolGroup.File, NullLogger.Instance);

	private static Roster RosterOf(String coordinator, params AgentEntryConfig[] agents)
		=> Roster.Build(new AgentConfig { Workflow = new WorkflowConfig { Coordinator = coordinator, Agents = [.. agents] } }, ["Read", "Write"]);

	private static Func<ProviderConfig, IChatClient> Clients(Dictionary<String, IChatClient> byDeployment)
		=> provider => byDeployment[provider.DeploymentName];

	[Fact]
	public void One_Handle_Per_Entry_Holding_Its_Allowlist_And_One_Delegation_Tool_Per_Target()
	{
		Roster roster = RosterOf("boss", Entry("boss", delegates: ["reader", "writer"]), Entry("reader", ["Read"]), Entry("writer", ["Read", "Write"]));
		Dictionary<String, IChatClient> clients = new() { ["boss"] = new RecordingChatClient(), ["reader"] = new RecordingChatClient(), ["writer"] = new RecordingChatClient() };

		using AgentRegistry registry = new(roster, Catalog(Tool("Read"), Tool("Write")), Clients(clients));

		registry.Handles.Select(handle => handle.Name).Should().Equal("boss", "reader", "writer");
		registry.Get("boss").Tools.Select(tool => tool.Name).Should().Equal("delegate_to_reader", "delegate_to_writer");
		registry.Get("boss").Tools.Select(tool => tool.Description).Should().Equal("the reader", "the writer");
		registry.Get("boss").Tools.OfType<ToolFacade>().Should().OnlyContain(tool => tool.Group == ToolGroup.Delegation);
		registry.Get("reader").Tools.Select(tool => tool.Name).Should().Equal("Read");
		registry.Get("writer").Tools.Select(tool => tool.Name).Should().Equal("Read", "Write");
		new StaticWorkflowRoute(roster, registry).Coordinator.Should().BeSameAs(registry.Get("boss"));
	}

	[Fact]
	public async Task A_Delegation_Runs_The_Target_On_The_Request_Alone_And_Returns_Its_Text()
	{
		Roster roster = RosterOf("boss", Entry("boss", delegates: ["reader"]), Entry("reader", ["Read"]));
		ScriptedChatClient boss = new(
			ScriptedChatClient.Call("delegate_to_reader", new() { ["request"] = "what is in notes.md?" }),
			ScriptedChatClient.Text("The reader says: a list."));
		ScriptedChatClient reader = new(ScriptedChatClient.Text("a list"));
		using AgentRegistry registry = new(roster, Catalog(Tool("Read")), Clients(new() { ["boss"] = boss, ["reader"] = reader }));

		AgentResponse response = await registry.Get("boss").Agent.RunAsync("tell me about notes.md");

		response.Text.Should().Be("The reader says: a list.");
		reader.Calls.Should().ContainSingle();
		reader.Calls[0].Messages.Where(message => message.Role == ChatRole.User).Select(message => message.Text).Should().Equal("what is in notes.md?");
		reader.Calls[0].Options!.Instructions.Should().StartWith("You are reader, an assistant that the reader.");
		reader.Calls[0].Options!.Tools!.Select(tool => tool.Name).Should().Equal("Read");
		FunctionResultContent result = boss.Calls[1].Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Single();
		result.Result!.ToString().Should().Be("a list");
	}

	/// <summary>The fatal contract one level up: the delegate's turn fails, so the caller's turn ends on the same exception.</summary>
	[Fact]
	public async Task A_Delegates_Failure_Ends_The_Callers_Turn()
	{
		IndexNotReadyException refusal = new("The index is still building.");
		AITool search = new ToolFacade(AIFunctionFactory.Create(new Func<String, String>(query => throw refusal), "Search"), ToolGroup.Search, NullLogger.Instance);
		ScriptedChatClient boss = new(
			ScriptedChatClient.Call("delegate_to_reader", new() { ["request"] = "find it" }),
			ScriptedChatClient.Text("never reached"));
		ScriptedChatClient reader = new(
			ScriptedChatClient.Call("Search", new() { ["query"] = "it" }),
			ScriptedChatClient.Text("never reached either"));
		using AgentRegistry registry = new(
			Roster.Build(new AgentConfig { Workflow = new WorkflowConfig { Coordinator = "boss", Agents = [Entry("boss", delegates: ["reader"]), Entry("reader", ["Search"])] } }, ["Search"]),
			Catalog(search),
			Clients(new() { ["boss"] = boss, ["reader"] = reader }));

		Func<Task> run = () => registry.Get("boss").Agent.RunAsync("find it for me");

		(await run.Should().ThrowAsync<IndexNotReadyException>()).Which.Should().BeSameAs(refusal);
		boss.Calls.Should().ContainSingle();
		reader.Calls.Should().ContainSingle();
	}

	[Fact]
	public void Disposing_The_Registry_Disposes_Every_Client_And_A_Failed_Build_Disposes_What_It_Made()
	{
		Roster roster = RosterOf("a", Entry("a", delegates: ["b"]), Entry("b"), Entry("c"));
		RecordingChatClient a = new();
		RecordingChatClient b = new();
		RecordingChatClient c = new();

		new AgentRegistry(roster, Catalog(), Clients(new() { ["a"] = a, ["b"] = b, ["c"] = c })).Dispose();
		Action failing = () => new AgentRegistry(roster, Catalog(), provider => provider.DeploymentName == "c"
			? throw new InvalidOperationException("Provider:ApiKey is required")
			: new RecordingChatClient());

		a.Disposed.Should().BeTrue();
		b.Disposed.Should().BeTrue();
		c.Disposed.Should().BeTrue();
		failing.Should().Throw<InvalidOperationException>().WithMessage("*ApiKey*");
	}

	[Fact]
	public void A_Delegation_Tool_Is_Named_Safely_After_Its_Target()
	{
		AgentRegistry.DelegationToolName("reader").Should().Be("delegate_to_reader");
		AgentRegistry.DelegationToolName("Folder Assistant (v2)").Should().Be("delegate_to_Folder_Assistant__v2_");
	}
}
