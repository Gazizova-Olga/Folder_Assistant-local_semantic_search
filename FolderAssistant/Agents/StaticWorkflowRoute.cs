namespace FolderAssistant.Agents;

/// <summary>
/// Where a turn goes (SPEC-100): every turn enters the roster's coordinator, and nothing else decides.
/// The route is static because the roster was checked whole at startup — the coordinator exists, and
/// it reaches the other agents through delegation tools the model chooses to call — so there is no
/// per-turn decision left to make, and a class that made one would be a second place routing could go
/// wrong.
/// </summary>
internal sealed class StaticWorkflowRoute
{
	private readonly Roster _roster;
	private readonly AgentRegistry _registry;

	public StaticWorkflowRoute(Roster roster, AgentRegistry registry)
	{
		ArgumentNullException.ThrowIfNull(roster);
		ArgumentNullException.ThrowIfNull(registry);
		this._roster = roster;
		this._registry = registry;
	}

	/// <summary>The coordinator's name — the roster's, so it can be said of a turn that failed before any agent was built.</summary>
	public String CoordinatorName => this._roster.Coordinator;

	/// <summary>The agent every turn enters.</summary>
	public AgentHandle Coordinator => this._registry.Get(this._roster.Coordinator);
}
