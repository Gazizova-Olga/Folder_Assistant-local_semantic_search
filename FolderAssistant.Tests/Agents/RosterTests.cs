using FluentAssertions;
using FolderAssistant.Agents;
using FolderAssistant.Extraction;
using FolderAssistant.Retrieval;
using FolderAssistant.Tests.Tools;
using FolderAssistant.Tools;
using Moq;

namespace FolderAssistant.Tests.Agents;

/// <summary>
/// The roster (SPEC-100): which of the three rosters the configuration yields, every rule that refuses
/// one, which agent a turn enters, and what an entry inherits from the root — the provider, field by
/// field — and what it never does: its role.
/// </summary>
public sealed class RosterTests
{
	private static readonly String[] Tools = ["InspectDirectory", "ReadFile", "Retrieve", "FindFiles", "SearchText", "Create", "Update", "ReplaceLines", "Delete", "SearchIndex", "FindFilesAbout"];

	private static AgentEntryConfig Entry(String name, String[]? tools = null, String[]? delegates = null, ProviderOverrideConfig? provider = null)
		=> new() { Name = name, Description = $"the {name}", Tools = [.. tools ?? []], Delegates = [.. delegates ?? []], Provider = provider };

	private static AgentConfig With(params AgentEntryConfig[] agents)
		=> new() { Workflow = new WorkflowConfig { Agents = [.. agents] } };

	[Fact]
	public void With_Nothing_Configured_One_Agent_From_The_Root_Holds_The_Read_And_Search_Tools()
	{
		AgentConfig config = new() { AgentName = "Archivist", AgentDescription = "keeps the notes", SystemPrompt = "Mine." };

		Roster roster = Roster.Build(config, Tools);

		roster.Agents.Should().ContainSingle();
		roster.Coordinator.Should().Be("Archivist");
		roster["Archivist"].Description.Should().Be("keeps the notes");
		roster["Archivist"].SystemPrompt.Should().Be("Mine.");
		roster["Archivist"].Tools.Should().Equal("InspectDirectory", "ReadFile", "Retrieve", "FindFiles", "SearchText", "SearchIndex", "FindFilesAbout");
		roster["Archivist"].Delegates.Should().BeEmpty();
		Roster.Build(new AgentConfig(), Tools).Coordinator.Should().Be(AgentFactory.DefaultName);
	}

	/// <summary>
	/// The grant is the whole safety posture — there is no approval gate — so the configuration that
	/// names no roster is the one that must not confer a mutation. Asserted against the real holders and
	/// **in both directions**: every read and search tool granted, every mutation tool withheld. A
	/// hand-written list checked one way passes while a new mutation tool is quietly granted.
	/// </summary>
	[Fact]
	public void The_Default_Grant_Holds_Every_Read_And_Search_Tool_And_No_Mutation_Tool()
	{
		using TempFolder root = new();
		ReadTools read = new(new WorkspacePathGuard(root.Path, ".folderassistant"), TextExtractorRegistry.Default, 1024);
		MutationTools mutate = new(new WorkspacePathGuard(root.Path, ".folderassistant"), new RecordingNotifier());
		SearchTools search = new(Mock.Of<IRetrievalQuery>(), "db", new PassageBuilder(root.Path, TextExtractorRegistry.Default), new TokenBudgetContextReducer());
		String[] readable = [.. ToolReflection.Reflect(read).Concat(ToolReflection.Reflect(search)).Select(static tool => tool.Name)];
		String[] mutating = [.. ToolReflection.Reflect(mutate).Select(static tool => tool.Name)];

		IReadOnlyList<String> granted = Roster.Build(new AgentConfig(), [.. readable, .. mutating]).Agents.Single().Tools;

		granted.Should().BeEquivalentTo(readable);
		granted.Should().NotIntersectWith(mutating);
		Roster.ReadOnlyTools.Should().BeEquivalentTo(readable);
	}

	/// <summary>
	/// The ability to change the folder is something an operator names. Both ways of asking for it grant
	/// it; the absence of configuration grants none of it.
	/// </summary>
	[Fact]
	public void A_Mutation_Tool_Is_Granted_Only_Where_It_Was_Asked_For()
	{
		AgentConfig none = new();
		AgentConfig viaRoster = new() { Workflow = new WorkflowConfig { UseDefaultRoster = true, Coordinator = Roster.OrchestratorName } };
		AgentConfig viaEntries = new() { Workflow = new WorkflowConfig { Agents = [Entry("solo", ["ReadFile", "Delete"])] } };

		Roster.Build(none, Tools).Agents.SelectMany(agent => agent.Tools).Should().NotContain("Delete");
		Roster.Build(viaRoster, Tools).Agents.SelectMany(agent => agent.Tools).Should().Contain("Delete");
		Roster.Build(viaEntries, Tools).Agents.SelectMany(agent => agent.Tools).Should().Contain("Delete");
	}

	[Fact]
	public void The_Default_Roster_Is_An_Orchestrator_Over_A_Reader_And_A_Mutator()
	{
		AgentConfig config = new() { Workflow = new WorkflowConfig { UseDefaultRoster = true, Coordinator = Roster.OrchestratorName } };

		Roster roster = Roster.Build(config, Tools);

		roster.Agents.Select(agent => agent.Name).Should().Equal(Roster.OrchestratorName, Roster.ReaderName, Roster.MutatorName);
		roster.Coordinator.Should().Be(Roster.OrchestratorName);
		roster[Roster.OrchestratorName].Tools.Should().BeEmpty();
		roster[Roster.OrchestratorName].Delegates.Should().Equal(Roster.ReaderName, Roster.MutatorName);
		roster[Roster.OrchestratorName].SystemPrompt.Should().Contain(Roster.ReaderName).And.Contain(Roster.MutatorName).And.Contain("TOOL_FAILED:");
		roster[Roster.ReaderName].Tools.Should().Equal("InspectDirectory", "ReadFile", "Retrieve", "FindFiles", "SearchText", "SearchIndex", "FindFilesAbout");
		roster[Roster.MutatorName].Tools.Should().Equal("ReadFile", "Retrieve", "Create", "Update", "ReplaceLines", "Delete");
		roster[Roster.ReaderName].Tools.Should().NotContain(roster[Roster.MutatorName].Tools.Except(roster[Roster.ReaderName].Tools));
	}

	/// <summary>The default roster is the default nowhere yet: its cost against the single agent is unmeasured.</summary>
	[Fact]
	public void The_Default_Roster_Is_Opt_In_Until_Measured()
	{
		new WorkflowConfig().UseDefaultRoster.Should().BeFalse();
		Roster.Build(new AgentConfig(), Tools).Agents.Should().ContainSingle();
	}

	[Fact]
	public void Configured_Agents_Replace_The_Default_Roster_Whole()
	{
		AgentConfig config = new() { Workflow = new WorkflowConfig { UseDefaultRoster = true, Agents = [Entry("solo", ["ReadFile"])] } };

		Roster roster = Roster.Build(config, Tools);

		roster.Agents.Select(agent => agent.Name).Should().Equal("solo");
		roster.Coordinator.Should().Be("solo");
	}

	[Fact]
	public void A_Configured_Coordinator_Must_Exist_And_Several_Agents_Need_One()
	{
		AgentConfig unnamed = With(Entry("a"), Entry("b"));
		AgentConfig wrong = With(Entry("a")) with { Workflow = new WorkflowConfig { Agents = [Entry("a")], Coordinator = "z" } };
		AgentConfig named = With(Entry("a"), Entry("b")) with { Workflow = new WorkflowConfig { Agents = [Entry("a"), Entry("b")], Coordinator = "b" } };

		Action several = () => Roster.Build(unnamed, Tools);
		Action missing = () => Roster.Build(wrong, Tools);

		several.Should().Throw<InvalidOperationException>().WithMessage("*Coordinator is required*a, b*");
		missing.Should().Throw<InvalidOperationException>().WithMessage("*'z' is not in the roster*");
		Roster.Build(named, Tools).Coordinator.Should().Be("b");
	}

	[Fact]
	public void A_Repeated_Name_A_Blank_Name_And_A_Missing_Description_Are_Refused()
	{
		Action repeated = () => Roster.Build(With(Entry("a"), Entry("a")), Tools);
		Action blank = () => Roster.Build(With(Entry("")), Tools);
		Action undescribed = () => Roster.Build(With(new AgentEntryConfig { Name = "a" }), Tools);

		repeated.Should().Throw<InvalidOperationException>().WithMessage("*two agents are named 'a'*");
		blank.Should().Throw<InvalidOperationException>().WithMessage("*needs a Name*");
		undescribed.Should().Throw<InvalidOperationException>().WithMessage("*'a' needs a Description*");
	}

	[Fact]
	public void An_Unknown_Tool_Is_Refused_With_The_Names_That_Exist()
	{
		Action build = () => Roster.Build(With(Entry("a", ["ReadFile", "Readfile"])), Tools);

		build.Should().Throw<InvalidOperationException>().WithMessage("*'a' names a tool 'Readfile' that does not exist*ReadFile*");
	}

	[Fact]
	public void Self_Delegation_And_An_Unknown_Delegate_Are_Refused()
	{
		Action self = () => Roster.Build(With(Entry("a", delegates: ["a"])), Tools);
		Action unknown = () => Roster.Build(With(Entry("a", delegates: ["ghost"])), Tools);

		self.Should().Throw<InvalidOperationException>().WithMessage("*'a' delegates to itself*");
		unknown.Should().Throw<InvalidOperationException>().WithMessage("*'a' delegates to 'ghost', which is not in the roster*");
	}

	[Fact]
	public void A_Delegation_Cycle_Is_Refused_And_Named()
	{
		AgentConfig two = With(Entry("a", delegates: ["b"]), Entry("b", delegates: ["a"])) with { Workflow = new WorkflowConfig { Agents = [Entry("a", delegates: ["b"]), Entry("b", delegates: ["a"])], Coordinator = "a" } };
		AgentConfig three = new() { Workflow = new WorkflowConfig { Coordinator = "a", Agents = [Entry("a", delegates: ["b"]), Entry("b", delegates: ["c"]), Entry("c", delegates: ["a"])] } };
		AgentConfig diamond = new() { Workflow = new WorkflowConfig { Coordinator = "a", Agents = [Entry("a", delegates: ["b", "c"]), Entry("b", delegates: ["d"]), Entry("c", delegates: ["d"]), Entry("d")] } };

		Action pair = () => Roster.Build(two, Tools);
		Action triangle = () => Roster.Build(three, Tools);

		pair.Should().Throw<InvalidOperationException>().WithMessage("*cycle: a -> b -> a*");
		triangle.Should().Throw<InvalidOperationException>().WithMessage("*cycle: a -> b -> c -> a*");
		Roster.Build(diamond, Tools).Agents.Should().HaveCount(4);
	}

	/// <summary>One case per provider field: declared, the agent's own; undeclared, the root's.</summary>
	[Theory]
	[InlineData("Type")]
	[InlineData("Endpoint")]
	[InlineData("ApiKey")]
	[InlineData("DeploymentName")]
	[InlineData("Temperature")]
	[InlineData("MaxTokens")]
	public void An_Agent_Inherits_Each_Provider_Field_It_Does_Not_Declare(String field)
	{
		ProviderConfig root = new()
		{
			Type = ProviderConfig.AiProviderType.OpenAI,
			Endpoint = "http://root/v1",
			ApiKey = "root-key",
			DeploymentName = "root-model",
			Temperature = 0.1,
			MaxTokens = 100,
		};
		ProviderOverrideConfig declared = field switch
		{
			"Type" => new() { Type = ProviderConfig.AiProviderType.Azure },
			"Endpoint" => new() { Endpoint = "http://own/v1" },
			"ApiKey" => new() { ApiKey = "own-key" },
			"DeploymentName" => new() { DeploymentName = "own-model" },
			"Temperature" => new() { Temperature = 0.9 },
			_ => new() { MaxTokens = 900 },
		};
		// The root with exactly the one declared field changed: the agent's provider must equal it whole,
		// so a field that stopped being inherited shows up as a difference rather than being masked.
		ProviderConfig expected = field switch
		{
			"Type" => root with { Type = ProviderConfig.AiProviderType.Azure },
			"Endpoint" => root with { Endpoint = "http://own/v1" },
			"ApiKey" => root with { ApiKey = "own-key" },
			"DeploymentName" => root with { DeploymentName = "own-model" },
			"Temperature" => root with { Temperature = 0.9 },
			_ => root with { MaxTokens = 900 },
		};
		AgentConfig config = new() { Provider = root, Workflow = new WorkflowConfig { Agents = [Entry("own", provider: declared), Entry("plain")], Coordinator = "own" } };

		Roster roster = Roster.Build(config, Tools);

		roster["own"].Provider.Should().Be(expected, $"the declared {field} is the agent's own and every other field the root's");
		roster["plain"].Provider.Should().Be(root);
	}

	[Fact]
	public void The_Role_Is_Never_Inherited()
	{
		AgentConfig config = new()
		{
			AgentName = "Root", AgentDescription = "the root's description", SystemPrompt = "the root's prompt",
			Workflow = new WorkflowConfig { Agents = [Entry("own")] },
		};

		AgentDefinition own = Roster.Build(config, Tools)["own"];

		own.Name.Should().Be("own");
		own.Description.Should().Be("the own");
		own.SystemPrompt.Should().BeNull();
	}
}
