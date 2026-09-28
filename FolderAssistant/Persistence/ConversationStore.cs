using System.Globalization;
using System.Text.Json;
using FolderAssistant.Agents;
using Microsoft.Data.Sqlite;

namespace FolderAssistant.Persistence;

/// <summary>One conversation, as a reader of the database sees it from outside.</summary>
internal sealed record ConversationSummary(
	String ConversationId,
	String CreatedUtc,
	String UpdatedUtc,
	Int64 Messages);

/// <summary>Conversations, and whether the list is all of them.</summary>
internal sealed record ConversationPage(IReadOnlyList<ConversationSummary> Conversations, Boolean Truncated);

/// <summary>One stored message, still in the framework's form: interpreting it is the reader's.</summary>
internal sealed record StoredMessage(Int64 Seq, String AgentName, String MessageJson, String CreatedUtc);

/// <summary>
/// One conversation and a window onto its messages. <paramref name="OmittedFromStart"/> is how many
/// earlier messages the window left out, which is zero when it is the whole conversation.
/// </summary>
internal sealed record ConversationTranscript(
	ConversationSummary Conversation,
	IReadOnlyList<StoredMessage> Messages,
	Int64 OmittedFromStart);

/// <summary>
/// The conversation database's writer and reader: the messages a conversation is made of, and the
/// serialized agent session each turn continues from (<c>SPEC-170</c>).
///
/// <para>
/// One type over both, for the reason <see cref="FolderIndexStore"/> serves two seams over the index: it
/// is one database with one connection policy, and one place where a conversation's row is brought into
/// existence. Two stores would each need that statement, and a message written under a conversation row
/// the other side had not created yet fails on the foreign key.
/// </para>
///
/// <para>
/// <strong>The messages are what the model is given on the next turn</strong>, not a record kept for
/// display: the framework's history provider reads them back through
/// <see cref="Agents.SqliteChatHistoryProvider"/>. So a row holds the serialized message exactly as it was
/// handed over, and this type neither renders nor interprets it.
/// </para>
///
/// <para>
/// The calls are asynchronous because the seams are, not because the work is — this provider's async
/// methods are its synchronous ones behind a state machine, the same shape and the same reasoning as the
/// index store. What is different here is where the work happens: on the request path, once per turn,
/// which is why the database is its own file.
/// </para>
/// </summary>
internal sealed class ConversationStore : IAgentSessionStore
{
	private readonly String _databasePath;

	public ConversationStore(String databasePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

		this._databasePath = databasePath;
	}

	/// <summary>
	/// One agent's messages in a conversation, oldest first, as they were stored.
	///
	/// <para>
	/// Filtered by agent because a session is: two agents serving one conversation each continue their own
	/// history, and handing one the other's would put words in its mouth that it never said.
	/// </para>
	/// </summary>
	public Task<IReadOnlyList<String>> ReadMessagesAsync(
		String conversationId,
		String agentName,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
		ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
		cancellationToken.ThrowIfCancellationRequested();

		List<String> messages = [];

		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(this._databasePath);
		using SqliteCommand command = connection.CreateCommand();

		command.CommandText = """
			SELECT message_json FROM message
			WHERE conversation_id = $conversation AND agent_name = $agent
			ORDER BY seq;
			""";
		command.Parameters.AddWithValue("$conversation", conversationId);
		command.Parameters.AddWithValue("$agent", agentName);

		using SqliteDataReader reader = command.ExecuteReader();

		while (reader.Read())
		{
			messages.Add(reader.GetString(0));
		}

		return Task.FromResult<IReadOnlyList<String>>(messages);
	}

	/// <summary>
	/// Appends one run's messages, in the order given, under sequences taken from the conversation's own
	/// counter.
	///
	/// <para>
	/// The whole run lands in one transaction, and the counter is advanced by as many numbers as this write
	/// holds rather than one at a time: a turn's messages are one fact — a question, the tool calls it
	/// caused, the answer — and half of them is a conversation that reads as though the model ignored its
	/// own tools. <c>MAX(seq) + 1</c> is what the counter exists to avoid, since two turns of one
	/// conversation are not ordered against each other and both would read the same maximum.
	/// </para>
	/// </summary>
	public Task AppendMessagesAsync(
		String conversationId,
		String agentName,
		IReadOnlyList<String> messagesJson,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
		ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
		ArgumentNullException.ThrowIfNull(messagesJson);
		cancellationToken.ThrowIfCancellationRequested();

		if (messagesJson.Count == 0)
		{
			return Task.CompletedTask;
		}

		String now = UtcNow();

		using SqliteConnection connection = FolderDatabaseConnection.OpenWrite(this._databasePath);
		using SqliteTransaction transaction = connection.BeginTransaction();

		EnsureConversation(connection, transaction, conversationId, now);

		Int64 first = ReserveSequences(connection, transaction, conversationId, messagesJson.Count, now);

		using (SqliteCommand insert = connection.CreateCommand())
		{
			insert.Transaction = transaction;
			insert.CommandText = """
				INSERT INTO message (conversation_id, seq, agent_name, message_json, created_utc)
				VALUES ($conversation, $seq, $agent, $message, $now);
				""";

			insert.Parameters.AddWithValue("$conversation", conversationId);
			SqliteParameter seq = insert.Parameters.Add("$seq", SqliteType.Integer);
			insert.Parameters.AddWithValue("$agent", agentName);
			SqliteParameter message = insert.Parameters.Add("$message", SqliteType.Text);
			insert.Parameters.AddWithValue("$now", now);

			for (Int32 index = 0; index < messagesJson.Count; index++)
			{
				seq.Value = first + index;
				message.Value = messagesJson[index];
				insert.ExecuteNonQuery();
			}
		}

		transaction.Commit();

		return Task.CompletedTask;
	}

	/// <summary>
	/// Every conversation the database holds, most recently touched first, with how many messages each
	/// carries.
	///
	/// <para>
	/// Bounded by <paramref name="max"/>, and the caller is told whether the bound applied rather than
	/// left to infer it from a round number.
	/// </para>
	/// </summary>
	public Task<ConversationPage> ListConversationsAsync(Int32 max, CancellationToken cancellationToken = default)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
		cancellationToken.ThrowIfCancellationRequested();

		List<ConversationSummary> conversations = [];

		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(this._databasePath);
		using SqliteCommand command = connection.CreateCommand();

		// One more than asked for, which is how the bound is reported without a second count query.
		command.CommandText = """
			SELECT c.conversation_id, c.created_utc, c.updated_utc,
			       (SELECT COUNT(*) FROM message m WHERE m.conversation_id = c.conversation_id)
			FROM conversation c
			ORDER BY c.updated_utc DESC, c.conversation_id
			LIMIT $limit;
			""";
		command.Parameters.AddWithValue("$limit", max + 1);

		using SqliteDataReader reader = command.ExecuteReader();

		while (reader.Read())
		{
			conversations.Add(new ConversationSummary(
				reader.GetString(0),
				reader.GetString(1),
				reader.GetString(2),
				reader.GetInt64(3)));
		}

		Boolean truncated = conversations.Count > max;
		if (truncated)
		{
			conversations.RemoveAt(conversations.Count - 1);
		}

		return Task.FromResult(new ConversationPage(conversations, truncated));
	}

	/// <summary>
	/// One conversation's messages in order, across every agent that served it, or null when there is no
	/// such conversation.
	///
	/// <para>
	/// Null and empty are kept apart deliberately: a conversation nobody started is not the same as one
	/// whose turn failed before it stored anything, and a reader shown an empty transcript for a name it
	/// mistyped would believe the name.
	/// </para>
	///
	/// <para>
	/// A conversation longer than <paramref name="max"/> yields its <em>most recent</em> messages and says
	/// how many it left out. Reading a long conversation from its beginning stops at the part nobody is
	/// looking for; where it got to is what a person asks for, and the count is what keeps the answer
	/// honest about being a window.
	/// </para>
	/// </summary>
	public Task<ConversationTranscript?> ReadConversationAsync(
		String conversationId,
		Int32 max,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
		cancellationToken.ThrowIfCancellationRequested();

		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(this._databasePath);

		ConversationSummary? summary = ReadSummary(connection, conversationId);
		if (summary is null)
		{
			return Task.FromResult<ConversationTranscript?>(null);
		}

		List<StoredMessage> messages = [];

		using (SqliteCommand command = connection.CreateCommand())
		{
			// The last $limit by sequence, put back in order below: the window is the end of the
			// conversation, and SQLite gives it cheaply in reverse.
			command.CommandText = """
				SELECT seq, agent_name, message_json, created_utc FROM message
				WHERE conversation_id = $conversation
				ORDER BY seq DESC
				LIMIT $limit;
				""";
			command.Parameters.AddWithValue("$conversation", conversationId);
			command.Parameters.AddWithValue("$limit", max);

			using SqliteDataReader reader = command.ExecuteReader();

			while (reader.Read())
			{
				messages.Add(new StoredMessage(
					reader.GetInt64(0),
					reader.GetString(1),
					reader.GetString(2),
					reader.GetString(3)));
			}
		}

		messages.Reverse();

		return Task.FromResult<ConversationTranscript?>(new ConversationTranscript(
			summary,
			messages,
			OmittedFromStart: Math.Max(0, summary.Messages - messages.Count)));
	}

	/// <summary>
	/// Removes one conversation whole — its messages and every agent's session with it — and reports
	/// whether there was one.
	///
	/// <para>
	/// One statement, because the cascade is declared on the rows that hang from a conversation: three
	/// deletes could half-succeed and leave a session for a conversation that no longer exists.
	/// </para>
	/// </summary>
	public Task<Boolean> DeleteConversationAsync(String conversationId, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
		cancellationToken.ThrowIfCancellationRequested();

		using SqliteConnection connection = FolderDatabaseConnection.OpenWrite(this._databasePath);
		using SqliteCommand command = connection.CreateCommand();

		command.CommandText = "DELETE FROM conversation WHERE conversation_id = $conversation;";
		command.Parameters.AddWithValue("$conversation", conversationId);

		return Task.FromResult(command.ExecuteNonQuery() > 0);
	}

	// ── IAgentSessionStore ─────────────────────────────────────────────────────

	public Task<JsonElement?> LoadAsync(String agentName, String conversationId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
		ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
		cancellationToken.ThrowIfCancellationRequested();

		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(this._databasePath);
		using SqliteCommand command = connection.CreateCommand();

		command.CommandText = """
			SELECT session_json FROM session_state
			WHERE agent_name = $agent AND conversation_id = $conversation;
			""";
		command.Parameters.AddWithValue("$agent", agentName);
		command.Parameters.AddWithValue("$conversation", conversationId);

		if (command.ExecuteScalar() is not String stored)
		{
			return Task.FromResult<JsonElement?>(null);
		}

		// Parsed here rather than by the caller, so a blob that is not JSON at all is the same failure as
		// one the framework cannot read: the turn's loader treats both as a session to start again.
		using JsonDocument document = JsonDocument.Parse(stored);

		return Task.FromResult<JsonElement?>(document.RootElement.Clone());
	}

	public Task SaveAsync(String agentName, String conversationId, JsonElement session, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
		ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
		cancellationToken.ThrowIfCancellationRequested();

		String now = UtcNow();

		using SqliteConnection connection = FolderDatabaseConnection.OpenWrite(this._databasePath);
		using SqliteTransaction transaction = connection.BeginTransaction();

		EnsureConversation(connection, transaction, conversationId, now);

		using (SqliteCommand command = connection.CreateCommand())
		{
			command.Transaction = transaction;
			command.CommandText = """
				INSERT INTO session_state (agent_name, conversation_id, session_json, updated_utc)
				VALUES ($agent, $conversation, $session, $now)
				ON CONFLICT(agent_name, conversation_id) DO UPDATE SET
					session_json = excluded.session_json,
					updated_utc = excluded.updated_utc;
				""";
			command.Parameters.AddWithValue("$agent", agentName);
			command.Parameters.AddWithValue("$conversation", conversationId);
			command.Parameters.AddWithValue("$session", session.GetRawText());
			command.Parameters.AddWithValue("$now", now);
			command.ExecuteNonQuery();
		}

		transaction.Commit();

		return Task.CompletedTask;
	}

	/// <summary>
	/// Brings the conversation's row into existence, or moves its last-touched time.
	///
	/// <para>
	/// Both writers call it, and neither can assume the other went first: a run's messages are stored
	/// before the session is saved, and a turn that failed saves no session at all.
	/// </para>
	/// </summary>
	private static void EnsureConversation(
		SqliteConnection connection,
		SqliteTransaction transaction,
		String conversationId,
		String now)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = """
			INSERT INTO conversation (conversation_id, created_utc, updated_utc)
			VALUES ($conversation, $now, $now)
			ON CONFLICT(conversation_id) DO UPDATE SET updated_utc = excluded.updated_utc;
			""";
		command.Parameters.AddWithValue("$conversation", conversationId);
		command.Parameters.AddWithValue("$now", now);
		command.ExecuteNonQuery();
	}

	/// <summary>
	/// Takes <paramref name="count"/> consecutive sequence numbers and returns the first of them.
	///
	/// <para>
	/// One statement, so the reservation is the advance: read-then-write would hand two concurrent turns
	/// the same block. The counter is the one value they contend on.
	/// </para>
	/// </summary>
	private static Int64 ReserveSequences(
		SqliteConnection connection,
		SqliteTransaction transaction,
		String conversationId,
		Int32 count,
		String now)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = """
			UPDATE conversation
			SET next_seq = next_seq + $count, updated_utc = $now
			WHERE conversation_id = $conversation
			RETURNING next_seq - $count;
			""";
		command.Parameters.AddWithValue("$count", count);
		command.Parameters.AddWithValue("$conversation", conversationId);
		command.Parameters.AddWithValue("$now", now);

		return command.ExecuteScalar() is Int64 first
			? first
			: throw new InvalidOperationException(
				$"The conversation '{conversationId}' has no row to take message sequences from.");
	}

	private static ConversationSummary? ReadSummary(SqliteConnection connection, String conversationId)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = """
			SELECT c.conversation_id, c.created_utc, c.updated_utc,
			       (SELECT COUNT(*) FROM message m WHERE m.conversation_id = c.conversation_id)
			FROM conversation c
			WHERE c.conversation_id = $conversation;
			""";
		command.Parameters.AddWithValue("$conversation", conversationId);

		using SqliteDataReader reader = command.ExecuteReader();

		return reader.Read()
			? new ConversationSummary(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3))
			: null;
	}

	private static String UtcNow() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
}
