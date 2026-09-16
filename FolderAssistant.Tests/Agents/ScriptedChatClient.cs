using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace FolderAssistant.Tests.Agents;

/// <summary>
/// A chat client that answers each call with the next scripted message — a tool call, then a text — and
/// records what it was asked, so the tool-calling loop can be driven through a real agent without a
/// model: the test decides which tool the "model" calls, and reads back what the loop handed it.
/// </summary>
internal sealed class ScriptedChatClient : IChatClient
{
	private readonly Queue<ChatMessage> _script;

	public ScriptedChatClient(params ChatMessage[] script)
	{
		this._script = new Queue<ChatMessage>(script);
	}

	public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];

	public static ChatMessage Call(String tool, Dictionary<String, Object?> arguments)
		=> new(ChatRole.Assistant, [new FunctionCallContent($"call-{tool}", tool, arguments)]);

	public static ChatMessage Text(String text) => new(ChatRole.Assistant, text);

	public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
	{
		this.Calls.Add(([.. messages], options));

		return Task.FromResult(new ChatResponse(this.Next()));
	}

	public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		this.Calls.Add(([.. messages], options));
		await Task.Yield();
		ChatMessage next = this.Next();
		yield return new ChatResponseUpdate(next.Role, next.Contents);
	}

	public Object? GetService(Type serviceType, Object? serviceKey = null)
		=> serviceType.IsInstanceOfType(this) ? this : null;

	public void Dispose()
	{
	}

	private ChatMessage Next()
	{
		if (this._script.Count == 0)
		{
			throw new InvalidOperationException("The script ran out: the loop asked for one more model turn than the test scripted.");
		}

		return this._script.Dequeue();
	}
}
