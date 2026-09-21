namespace FolderAssistant.Agents;

/// <summary>One agent as the roster defines it: its role, its tool allowlist, its delegates and its effective provider.</summary>
internal sealed record AgentDefinition(
	String Name,
	String Description,
	String? SystemPrompt,
	IReadOnlyList<String> Tools,
	IReadOnlyList<String> Delegates,
	ProviderConfig Provider);

/// <summary>
/// The validated roster (SPEC-100): which agents exist, what each may call and delegate to, and which
/// one every turn enters. Built once from the configuration and checked whole, at startup, because
/// nothing bounds delegation at runtime — a cycle would recurse until the stack ended, and a coordinator
/// that does not exist would be found on the first turn — so the graph is refused before a turn can run.
///
/// <para>
/// Three rosters are possible, in this order of precedence. Agents the operator configured are the
/// roster, whole; the default roster from code, when asked for; otherwise one agent, synthesized from
/// the root configuration, holding every tool. The default lives here rather than in
/// <c>appsettings.json</c> because configuration arrays merge by index: a shipped roster would be merged
/// <em>into</em> an operator's entries, never replaced by them.
/// </para>
/// </summary>
internal sealed class Roster
{
	internal const String OrchestratorName = "orchestrator";
	internal const String ReaderName = "reader";
	internal const String MutatorName = "mutator";

	private static readonly String[] ReaderTools = ["InspectDirectory", "ReadFile", "Retrieve", "FindFiles", "SearchText", "FindFilesAbout"];
	private static readonly String[] MutatorTools = ["ReadFile", "Retrieve", "Create", "Update", "ReplaceLines", "Delete"];

	private readonly Dictionary<String, AgentDefinition> _byName;

	private Roster(IReadOnlyList<AgentDefinition> agents, String coordinator)
	{
		this.Agents = agents;
		this.Coordinator = coordinator;
		this._byName = agents.ToDictionary(static agent => agent.Name, StringComparer.Ordinal);
	}

	/// <summary>The agents, in roster order.</summary>
	public IReadOnlyList<AgentDefinition> Agents { get; }

	/// <summary>The name of the agent every turn enters.</summary>
	public String Coordinator { get; }

	public AgentDefinition this[String name] => this._byName[name];

	/// <summary>
	/// The roster the configuration asks for, validated against <paramref name="toolNames"/> — the
	/// names the catalog can grant. Every rule below throws <see cref="InvalidOperationException"/>
	/// naming what was wrong, and is meant to fail startup.
	/// </summary>
	public static Roster Build(AgentConfig config, IReadOnlyCollection<String> toolNames)
	{
		ArgumentNullException.ThrowIfNull(config);
		ArgumentNullException.ThrowIfNull(toolNames);

		List<AgentDefinition> agents;
		if (config.Workflow.Agents.Count > 0)
		{
			agents = [.. config.Workflow.Agents.Select(entry => FromEntry(entry, config.Provider))];
		}
		else if (config.Workflow.UseDefaultRoster)
		{
			agents = Default(config.Provider);
		}
		else
		{
			agents = [Single(config, toolNames)];
		}

		Validate(agents, toolNames);

		return new Roster(agents, ResolveCoordinator(agents, config.Workflow.Coordinator));
	}

	/// <summary>
	/// The default roster: an orchestrator with no tools that delegates to a reader holding every read
	/// and search tool and a mutator holding the four mutations and the two readers it needs to make an
	/// edit it has seen. The split is the point: which agent can change the folder is one line here.
	/// </summary>
	internal static List<AgentDefinition> Default(ProviderConfig provider) =>
	[
		new(
			OrchestratorName,
			"coordinates the reader and the mutator to answer questions about one folder on this machine",
			$"You are the orchestrator of {AgentFactory.DefaultName}, which {AgentFactory.DefaultDescription}. "
				+ $"You hold no tools of your own: to read or search the folder, delegate to '{ReaderName}'; to change a file, delegate to '{MutatorName}'. "
				+ "A delegate sees only the request you send it and nothing of this conversation, so send it everything it needs. "
				+ "Answer the user from what the delegates report, and say so when they report that the folder does not hold the answer. "
				+ "Never present something they did not find in the folder as if it came from it. "
				+ $"A report that begins with {ToolFacade.FailurePrefix.Trim()} is a failure: report it to the user as such, and do not answer around it.",
			[],
			[ReaderName, MutatorName],
			provider),
		new(
			ReaderName,
			"reads and searches the folder: lists directories, reads files, finds files by name or by what they are about, and searches their text",
			null,
			ReaderTools,
			[],
			provider),
		new(
			MutatorName,
			"changes files in the folder: creates a file, replaces text or a range of lines in one, or deletes a file or a directory, reading the file first where it needs to",
			null,
			MutatorTools,
			[],
			provider),
	];

	private static AgentDefinition Single(AgentConfig config, IReadOnlyCollection<String> toolNames) => new(
		String.IsNullOrWhiteSpace(config.AgentName) ? AgentFactory.DefaultName : config.AgentName,
		String.IsNullOrWhiteSpace(config.AgentDescription) ? AgentFactory.DefaultDescription : config.AgentDescription,
		config.SystemPrompt,
		[.. toolNames],
		[],
		config.Provider);

	private static AgentDefinition FromEntry(AgentEntryConfig entry, ProviderConfig root)
	{
		if (String.IsNullOrWhiteSpace(entry.Name))
		{
			throw new InvalidOperationException("Workflow:Agents: every agent needs a Name.");
		}

		if (String.IsNullOrWhiteSpace(entry.Description))
		{
			throw new InvalidOperationException($"Workflow:Agents: agent '{entry.Name}' needs a Description; it is what a delegation to it is described by.");
		}

		return new AgentDefinition(
			entry.Name,
			entry.Description,
			entry.SystemPrompt,
			[.. entry.Tools],
			[.. entry.Delegates],
			entry.Provider?.Apply(root) ?? root);
	}

	private static void Validate(List<AgentDefinition> agents, IReadOnlyCollection<String> toolNames)
	{
		HashSet<String> names = new(StringComparer.Ordinal);
		String? repeated = agents.Select(static agent => agent.Name).FirstOrDefault(name => !names.Add(name));
		if (repeated is not null)
		{
			throw new InvalidOperationException($"Workflow:Agents: two agents are named '{repeated}'.");
		}

		HashSet<String> known = new(toolNames, StringComparer.Ordinal);
		foreach (AgentDefinition agent in agents)
		{
			String? unknown = agent.Tools.FirstOrDefault(tool => !known.Contains(tool));
			if (unknown is not null)
			{
				throw new InvalidOperationException($"Workflow:Agents: agent '{agent.Name}' names a tool '{unknown}' that does not exist. The tools are: {String.Join(", ", toolNames)}.");
			}

			foreach (String target in agent.Delegates)
			{
				if (target == agent.Name)
				{
					throw new InvalidOperationException($"Workflow:Agents: agent '{agent.Name}' delegates to itself.");
				}

				if (!names.Contains(target))
				{
					throw new InvalidOperationException($"Workflow:Agents: agent '{agent.Name}' delegates to '{target}', which is not in the roster.");
				}
			}
		}

		RefuseCycles(agents);
	}

	/// <summary>
	/// A depth-first walk over the delegation edges. An agent on the current path reached again is a
	/// cycle, and the message names the path so the operator sees which edge to cut.
	/// </summary>
	private static void RefuseCycles(List<AgentDefinition> agents)
	{
		Dictionary<String, AgentDefinition> byName = agents.ToDictionary(static agent => agent.Name, StringComparer.Ordinal);
		HashSet<String> finished = new(StringComparer.Ordinal);
		List<String> path = [];

		foreach (AgentDefinition agent in agents)
		{
			Walk(agent);
		}

		void Walk(AgentDefinition agent)
		{
			if (finished.Contains(agent.Name))
			{
				return;
			}

			Int32 seen = path.IndexOf(agent.Name);
			if (seen >= 0)
			{
				throw new InvalidOperationException($"Workflow:Agents: delegation forms a cycle: {String.Join(" -> ", path.Skip(seen).Append(agent.Name))}.");
			}

			path.Add(agent.Name);
			foreach (String target in agent.Delegates)
			{
				Walk(byName[target]);
			}

			path.RemoveAt(path.Count - 1);
			finished.Add(agent.Name);
		}
	}

	private static String ResolveCoordinator(List<AgentDefinition> agents, String? configured)
	{
		if (!String.IsNullOrWhiteSpace(configured))
		{
			if (agents.TrueForAll(agent => agent.Name != configured))
			{
				throw new InvalidOperationException($"Workflow:Coordinator '{configured}' is not in the roster. The agents are: {String.Join(", ", agents.Select(static agent => agent.Name))}.");
			}

			return configured;
		}

		if (agents.Count == 1)
		{
			return agents[0].Name;
		}

		throw new InvalidOperationException($"Workflow:Coordinator is required when the roster holds more than one agent. The agents are: {String.Join(", ", agents.Select(static agent => agent.Name))}.");
	}
}
