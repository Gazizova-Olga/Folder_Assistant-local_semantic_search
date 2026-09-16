using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace FolderAssistant.Tests.Agents;

/// <summary>
/// A chat client that answers with a fixed text and records what it was asked — the messages, the
/// options and whether it was disposed — so the agent factory's wiring can be asserted without a model.
/// </summary>
internal sealed class RecordingChatClient : IChatClient
{
	public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];

	public Boolean Disposed { get; private set; }

	public String Reply { get; init; } = "recorded reply";

	public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
	{
		this.Calls.Add(([.. messages], options));

		return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, this.Reply)));
	}

	public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		this.Calls.Add(([.. messages], options));
		await Task.Yield();
		yield return new ChatResponseUpdate(ChatRole.Assistant, this.Reply);
	}

	public Object? GetService(Type serviceType, Object? serviceKey = null)
		=> serviceType.IsInstanceOfType(this) ? this : null;

	public void Dispose() => this.Disposed = true;
}
