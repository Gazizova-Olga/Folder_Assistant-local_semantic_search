using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FolderAssistant.Agents;

/// <summary>
/// The one thing a front end talks to (SPEC-100): the roster behind an <see cref="IChatClient"/>, so a
/// console, a browser surface and an HTTP endpoint all run a turn the same way and none of them knows
/// which agent answered.
///
/// <para>
/// Of the caller's options it reads the conversation id and nothing else — the model, the sampling and
/// the tools are each agent's own. A call that names no conversation starts one, and every response and
/// update says which conversation it belongs to, so the caller can name it on the next turn. The
/// conversation's earlier turns are the session's, not the caller's to resend: the messages of a call are
/// the new ones.
/// </para>
/// </summary>
internal sealed class WorkflowRunner : IChatClient
{
	private readonly IAgentExecution _execution;

	public WorkflowRunner(IAgentExecution execution)
	{
		ArgumentNullException.ThrowIfNull(execution);
		this._execution = execution;
	}

	public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(messages);

		String conversationId = ConversationOf(options);
		AgentResponse response = await this._execution.RunAsync(conversationId, [.. messages], cancellationToken).ConfigureAwait(false);

		ChatResponse chat = response.AsChatResponse();
		chat.ConversationId = conversationId;

		return chat;
	}

	public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(messages);

		return this.StreamAsync([.. messages], ConversationOf(options), cancellationToken);
	}

	public Object? GetService(Type serviceType, Object? serviceKey = null)
	{
		ArgumentNullException.ThrowIfNull(serviceType);

		return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
	}

	/// <summary>Nothing to end: the clients are the registry's, and the registry is the container's.</summary>
	public void Dispose()
	{
		// Owns nothing.
	}

	private static String ConversationOf(ChatOptions? options)
		=> String.IsNullOrWhiteSpace(options?.ConversationId) ? Guid.NewGuid().ToString("N") : options.ConversationId;

	private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(IReadOnlyList<ChatMessage> messages, String conversationId, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await foreach (AgentResponseUpdate update in this._execution.RunStreamingAsync(conversationId, messages, cancellationToken).ConfigureAwait(false))
		{
			ChatResponseUpdate chat = update.AsChatResponseUpdate();
			chat.ConversationId = conversationId;

			yield return chat;
		}
	}
}
