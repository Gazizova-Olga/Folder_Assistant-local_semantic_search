using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FolderAssistant.Agents;

/// <summary>
/// One configured agent and the chat client it talks through, owned together. The agent framework's
/// agent does not own its client, and a client is a disposable pipeline, so something has to hold both
/// and end both; this is it. Only synchronously disposable clients exist here, so there is no async
/// half to keep in step.
/// </summary>
internal sealed class AgentHandle : IDisposable
{
	private readonly IChatClient _client;

	public AgentHandle(String name, AIAgent agent, IChatClient client)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentNullException.ThrowIfNull(agent);
		ArgumentNullException.ThrowIfNull(client);
		this.Name = name;
		this.Agent = agent;
		this._client = client;
	}

	/// <summary>The agent's name, as configured: the key a registry holds it under.</summary>
	public String Name { get; }

	public AIAgent Agent { get; }

	public void Dispose() => this._client.Dispose();
}
