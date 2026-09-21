using Microsoft.Extensions.AI;

namespace FolderAssistant.Agents;

/// <summary>
/// Every tool the application can grant, by name, and the narrowing of that list to what one roster
/// entry allows (SPEC-100). The catalog is what makes "which agent can change the folder" a question
/// answered by reading one allowlist: the mutation tools are in here like any other, and an agent holds
/// exactly the names its entry lists.
/// </summary>
internal sealed class AgentToolCatalog
{
	private readonly Dictionary<String, AITool> _byName;

	public AgentToolCatalog(IReadOnlyList<AITool> tools)
	{
		ArgumentNullException.ThrowIfNull(tools);
		this._byName = new Dictionary<String, AITool>(StringComparer.Ordinal);
		foreach (AITool tool in tools)
		{
			if (!this._byName.TryAdd(tool.Name, tool))
			{
				throw new InvalidOperationException($"Two tools share one name: {tool.Name}. A model cannot tell them apart.");
			}
		}

		this.Names = [.. this._byName.Keys];
	}

	/// <summary>Every tool's name, in the order the tools were given.</summary>
	public IReadOnlyList<String> Names { get; }

	/// <summary>
	/// The tools named, in the order named. A name the catalog does not hold throws: an agent silently
	/// missing a capability its entry asked for would look like an agent that chose not to use it.
	/// </summary>
	public IReadOnlyList<AITool> Select(IReadOnlyList<String> names)
	{
		ArgumentNullException.ThrowIfNull(names);

		return [.. names.Select(name => this._byName.TryGetValue(name, out AITool? tool)
			? tool
			: throw new InvalidOperationException($"No tool is named '{name}'. The tools are: {String.Join(", ", this.Names)}."))];
	}
}
