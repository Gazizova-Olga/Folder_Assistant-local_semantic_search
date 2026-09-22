using System.Collections.Concurrent;
using System.Text.Json;

namespace FolderAssistant.Agents;

/// <summary>
/// Where an agent's serialized session is kept between turns (SPEC-100). Keyed by agent <b>and</b>
/// conversation: two agents serving one conversation hold two sessions, and a key of the conversation
/// alone would have each overwrite the other's.
/// </summary>
internal interface IAgentSessionStore
{
	/// <summary>The session as it was last saved, or null when this agent has none for the conversation.</summary>
	Task<JsonElement?> LoadAsync(String agentName, String conversationId, CancellationToken cancellationToken);

	Task SaveAsync(String agentName, String conversationId, JsonElement session, CancellationToken cancellationToken);
}

/// <summary>
/// Sessions held for the life of the process and no longer. It holds the serialized form rather than the
/// live object, so a turn that fails leaves the stored session exactly as the last good turn saved it.
/// Nothing bounds it: a conversation is kept until the process ends.
/// </summary>
internal sealed class InMemoryAgentSessionStore : IAgentSessionStore
{
	private readonly ConcurrentDictionary<(String Agent, String Conversation), JsonElement> _sessions = new();

	public Task<JsonElement?> LoadAsync(String agentName, String conversationId, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		return Task.FromResult<JsonElement?>(
			this._sessions.TryGetValue((agentName, conversationId), out JsonElement session) ? session : null);
	}

	public Task SaveAsync(String agentName, String conversationId, JsonElement session, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		// Cloned, so what is kept does not live on a document its producer may dispose.
		this._sessions[(agentName, conversationId)] = session.Clone();

		return Task.CompletedTask;
	}
}
