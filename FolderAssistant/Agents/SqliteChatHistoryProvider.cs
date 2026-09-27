using System.Text.Json;
using FolderAssistant.Persistence;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderAssistant.Agents;

/// <summary>
/// A conversation's history, kept in the conversation database through the framework's own seam
/// (<c>SPEC-170</c>): the framework asks for the history before each run and hands over the new messages
/// after it.
///
/// <para>
/// <strong>Why this seam and not a record of our own.</strong> An agent whose service keeps no history —
/// which is every provider this application talks to — gets its memory from a
/// <see cref="ChatHistoryProvider"/>, and the framework's default keeps that memory inside the session.
/// A projection of ours beside it would have been a second copy of one conversation: the messages the
/// model is given, and the messages a person is shown, free to diverge with nothing failing. Writing
/// through the seam moves that storage instead of adding a layer, and afterwards there is one copy.
/// </para>
///
/// <para>
/// <strong>What that costs is that this is on the prompt path.</strong> These rows are what the model is
/// handed next turn, so a write that loses part of a message changes what the model believes was said.
/// Hence a row holds the serialized <see cref="ChatMessage"/> — a tool call and its result are messages
/// too — and a row that cannot be read again <em>fails the turn</em> rather than being skipped. A skipped
/// message is a conversation with a hole in it, answered as though the missing turn never happened, which
/// is the silently plausible wrong answer this application is built to refuse. The session blob's rule is
/// deliberately the opposite (a fresh session, logged) because losing that costs memory, not truth.
/// </para>
///
/// <para>
/// <strong>One instance serves every agent and every session</strong>, as the framework requires, so it
/// holds no conversation of its own: which conversation a run belongs to is read from the session's state
/// bag, where <see cref="MicrosoftAgentExecution"/> puts it. A run with no conversation there — a
/// delegation, which the registry runs with no session at all — reads nothing and writes nothing, which is
/// what keeps a delegate's request out of the caller's conversation.
/// </para>
/// </summary>
internal sealed class SqliteChatHistoryProvider : ChatHistoryProvider
{
	/// <summary>
	/// Where the turn leaves the conversation's identifier for this provider to find. It is this
	/// application's identifier: a chat-completions service has no conversation of its own, and the
	/// invocation context carries only the agent and the session.
	/// </summary>
	internal const String ConversationIdKey = "folderassistant.conversationId";

	private readonly ConversationStore _store;
	private readonly ILogger _logger;

	public SqliteChatHistoryProvider(ConversationStore store, ILogger<SqliteChatHistoryProvider>? logger = null)
	{
		ArgumentNullException.ThrowIfNull(store);

		this._store = store;
		this._logger = logger ?? (ILogger)NullLogger.Instance;
	}

	/// <summary>
	/// The conversation this run belongs to, or null when it is not part of one — a delegation, which the
	/// registry runs with no session of its own.
	/// </summary>
	internal static String? ConversationIdOf(AgentSession? session)
	{
		return session is not null
			&& session.StateBag.TryGetValue(ConversationIdKey, out String? conversationId)
			&& !String.IsNullOrWhiteSpace(conversationId)
				? conversationId
				: null;
	}

	/// <summary>Puts the conversation where this provider reads it, on a session the turn is about to run.</summary>
	internal static void Remember(AgentSession session, String conversationId)
	{
		ArgumentNullException.ThrowIfNull(session);
		ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

		session.StateBag.SetValue(ConversationIdKey, conversationId);
	}

	protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
		InvokingContext context,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(context);

		String? conversationId = ConversationIdOf(context.Session);
		if (conversationId is null)
		{
			return [];
		}

		String agentName = AgentNameOf(context.Agent);

		IReadOnlyList<String> stored = await this._store
			.ReadMessagesAsync(conversationId, agentName, cancellationToken)
			.ConfigureAwait(false);

		ChatMessage[] history = new ChatMessage[stored.Count];

		for (Int32 index = 0; index < stored.Count; index++)
		{
			history[index] = Deserialize(stored[index], conversationId, agentName, index);
		}

		this._logger.LogDebug(
			"Conversation {ConversationId} gave agent {Agent} {Count} stored messages.",
			conversationId, agentName, history.Length);

		return history;
	}

	protected override async ValueTask StoreChatHistoryAsync(
		InvokedContext context,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(context);

		String? conversationId = ConversationIdOf(context.Session);
		if (conversationId is null)
		{
			return;
		}

		// The request messages here are the caller's new ones — the history this provider just supplied is
		// not among them, so appending both halves stores each message once. Measured against the framework
		// rather than taken from its documentation, which describes this property two ways.
		List<String> messages =
		[
			.. context.RequestMessages.Select(Serialize),
			.. (context.ResponseMessages ?? []).Select(Serialize),
		];

		await this._store
			.AppendMessagesAsync(conversationId, AgentNameOf(context.Agent), messages, cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// The options the framework itself serializes chat content with. Ours would round-trip the parts it
	/// knew about and quietly drop the rest, which on the prompt path is a message the model is told it
	/// sent in a form it never sent.
	/// </summary>
	private static String Serialize(ChatMessage message)
		=> JsonSerializer.Serialize(message, AIJsonUtilities.DefaultOptions);

	private static ChatMessage Deserialize(String json, String conversationId, String agentName, Int32 position)
	{
		try
		{
			return JsonSerializer.Deserialize<ChatMessage>(json, AIJsonUtilities.DefaultOptions)
				?? throw new InvalidOperationException("the stored message is null");
		}
		catch (Exception exception) when (exception is JsonException or InvalidOperationException or NotSupportedException)
		{
			throw new InvalidOperationException(
				$"Message {position + 1} of conversation '{conversationId}' for agent '{agentName}' could not be read "
					+ "back, so this turn would continue a conversation with a hole in it. Delete the conversation to "
					+ "start it again.",
				exception);
		}
	}

	/// <summary>
	/// The agent's name, and a run through an agent without one is a wiring mistake rather than a
	/// conversation to file under the empty string.
	/// </summary>
	private static String AgentNameOf(AIAgent agent)
		=> String.IsNullOrWhiteSpace(agent.Name)
			? throw new InvalidOperationException("An agent storing conversation history must have a name.")
			: agent.Name;
}
