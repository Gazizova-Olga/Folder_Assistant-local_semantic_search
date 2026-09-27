using System.Text.Json;
using FluentAssertions;
using FolderAssistant.Agents;
using FolderAssistant.Persistence;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace FolderAssistant.Tests.Agents;

/// <summary>
/// A conversation kept in the database through the framework's own history seam (SPEC-170): what the model
/// is given on the next turn comes from rows, and it comes back as it was sent.
///
/// <para>
/// These run a real <c>ChatClientAgent</c> over a scripted client, because the property under test is what
/// the framework hands the provider and what it then sends to the model. A test calling the provider's own
/// methods would assert our arithmetic and prove nothing about the seam.
/// </para>
/// </summary>
public sealed class SqliteChatHistoryProviderTests
{
	[Fact]
	public async Task A_Second_Turn_Is_Sent_The_First_Turns_Messages()
	{
		using Fixture fixture = new();

		await fixture.TurnAsync("c1", "what is in notes.md?");
		await fixture.TurnAsync("c1", "how long is it?");

		// The history first, then the new question — the order the framework merges them in.
		fixture.Client.Calls[1].Messages.Select(static message => message.Text)
			.Should().Equal("what is in notes.md?", "a list", "how long is it?");
	}

	/// <summary>
	/// The property the whole database exists for: a second process, holding nothing in memory, continues
	/// the conversation. The provider and the store are built fresh over the same file, which is what a
	/// restart is.
	/// </summary>
	[Fact]
	public async Task A_Conversation_Continues_In_A_Process_That_Holds_Nothing_In_Memory()
	{
		using TempFolder folder = new();
		String databasePath = Bootstrap(folder);

		using (Fixture first = new(databasePath))
		{
			await first.TurnAsync("c1", "what is in notes.md?");
		}

		using Fixture restarted = new(databasePath);

		await restarted.TurnAsync("c1", "how long is it?");

		restarted.Client.Calls[0].Messages.Select(static message => message.Text)
			.Should().Equal("what is in notes.md?", "a list", "how long is it?");
	}

	/// <summary>
	/// Two conversations do not bleed into each other, which is the assertion a store keyed on the agent
	/// alone would fail.
	/// </summary>
	[Fact]
	public async Task Another_Conversation_Is_Sent_Nothing_Of_The_First()
	{
		using Fixture fixture = new();

		await fixture.TurnAsync("c1", "what is in notes.md?");
		await fixture.TurnAsync("c2", "and you?");

		fixture.Client.Calls[1].Messages.Select(static message => message.Text).Should().Equal("and you?");
	}

	/// <summary>
	/// A tool call and its result are messages too, and they are what the next turn's context is made of. A
	/// row holding display text would lose them, and the model would read as though it had never called the
	/// tool — so the round trip is asserted on the content, not on the text.
	/// </summary>
	[Fact]
	public async Task A_Tool_Call_And_Its_Result_Round_Trip_Through_A_Row()
	{
		using Fixture fixture = new();

		FunctionCallContent call = new("call-1", "SearchIndex", new Dictionary<String, Object?> { ["query"] = "tides" });
		FunctionResultContent result = new("call-1", "two passages");

		await fixture.StoreAsync("c1", [
			new ChatMessage(ChatRole.Assistant, [call]),
			new ChatMessage(ChatRole.Tool, [result]),
		]);

		IReadOnlyList<ChatMessage> read = await fixture.ProvideAsync("c1");

		read.Should().HaveCount(2);
		read[0].Contents.Should().ContainSingle().Which.Should().BeOfType<FunctionCallContent>()
			.Which.Should().BeEquivalentTo(new { CallId = "call-1", Name = "SearchIndex" });
		// An argument comes back as a JsonElement rather than the String it went in as: that is how the
		// framework carries arguments, and a row storing text would have lost the structure around it.
		read[0].Contents.OfType<FunctionCallContent>().Single().Arguments!["query"]
			.Should().BeOfType<JsonElement>().Which.GetString().Should().Be("tides");
		read[1].Contents.Should().ContainSingle().Which.Should().BeOfType<FunctionResultContent>()
			.Which.CallId.Should().Be("call-1");
	}

	/// <summary>
	/// A row that cannot be read back fails the turn instead of being skipped. A skipped message is a
	/// conversation with a hole in it, answered as though the missing turn never happened — the plausible
	/// wrong answer this application refuses. The session blob's rule is the opposite on purpose, and the
	/// difference is that losing a blob costs memory rather than truth.
	/// </summary>
	[Fact]
	public async Task A_Message_That_Cannot_Be_Read_Back_Fails_The_Turn()
	{
		using Fixture fixture = new();

		await fixture.TurnAsync("c1", "what is in notes.md?");

		Corrupt(fixture.DatabasePath, "c1");

		Func<Task> next = () => fixture.TurnAsync("c1", "how long is it?");

		(await next.Should().ThrowAsync<InvalidOperationException>())
			.Which.Message.Should().Contain("c1").And.Contain("hole");
	}

	/// <summary>
	/// A run the turn did not name a conversation for — a delegation, which the registry runs with no
	/// session — reads nothing and writes nothing. Otherwise a delegate's request would be filed as part of
	/// the conversation that asked for it, and its answer read back as the coordinator's own words.
	/// </summary>
	[Fact]
	public async Task A_Run_With_No_Conversation_Reads_And_Writes_Nothing()
	{
		using Fixture fixture = new();

		await fixture.Agent.RunAsync("answer this on your own", cancellationToken: CancellationToken.None);

		MessageCount(fixture.DatabasePath).Should().Be(0);
		fixture.Client.Calls[0].Messages.Select(static message => message.Text).Should().Equal("answer this on your own");
	}

	/// <summary>
	/// The framework's own default keeps history inside the session, and that is what this replaces: with a
	/// provider set, the session carries no messages, so the rows are the only copy.
	/// </summary>
	[Fact]
	public async Task With_A_Provider_The_Session_Carries_No_Messages()
	{
		using Fixture fixture = new();

		AgentSession session = await fixture.Agent.CreateSessionAsync(CancellationToken.None);
		SqliteChatHistoryProvider.Remember(session, "c1");

		await fixture.Agent.RunAsync([new ChatMessage(ChatRole.User, "what is in notes.md?")], session, cancellationToken: CancellationToken.None);

		String serialized = (await fixture.Agent.SerializeSessionAsync(session, cancellationToken: CancellationToken.None)).GetRawText();

		serialized.Should().NotContain("notes.md", "the messages are in rows, not in the session");
		serialized.Should().Contain("c1", "what the session does carry is which conversation it belongs to");
		MessageCount(fixture.DatabasePath).Should().Be(2);
	}

	private static String Bootstrap(TempFolder folder)
		=> new ConversationDatabaseBootstrapper()
			.EnsureInitialized(folder.Path, new PersistenceConfig()).DatabasePath;

	private static Int64 MessageCount(String databasePath)
	{
		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(databasePath);
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT COUNT(*) FROM message;";

		return (Int64)command.ExecuteScalar()!;
	}

	/// <summary>Replaces the conversation's first stored message with something no deserializer will take.</summary>
	private static void Corrupt(String databasePath, String conversationId)
	{
		using SqliteConnection connection = FolderDatabaseConnection.OpenWrite(databasePath);
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = """
			UPDATE message SET message_json = '{"role":'
			WHERE conversation_id = $conversation AND seq = 1;
			""";
		command.Parameters.AddWithValue("$conversation", conversationId);
		command.ExecuteNonQuery().Should().Be(1);
	}

	/// <summary>A real agent over a scripted client, with the provider and the store over one database.</summary>
	private sealed class Fixture : IDisposable
	{
		private readonly TempFolder? _folder;
		private readonly ConversationStore _store;

		public Fixture()
			: this(null)
		{
		}

		public Fixture(String? databasePath)
		{
			if (databasePath is null)
			{
				this._folder = new TempFolder();
				databasePath = Bootstrap(this._folder);
			}

			this.DatabasePath = databasePath;
			this._store = new ConversationStore(databasePath);
			this.Provider = new SqliteChatHistoryProvider(this._store);
			this.Client = new ScriptedChatClient(
				ScriptedChatClient.Text("a list"),
				ScriptedChatClient.Text("three items"),
				ScriptedChatClient.Text("nothing yet"),
				ScriptedChatClient.Text("still nothing"));

			this.Agent = new ChatClientAgent(this.Client, new ChatClientAgentOptions
			{
				Name = "solo",
				Description = "answers",
				ChatHistoryProvider = this.Provider,
			});
		}

		public String DatabasePath { get; }

		public SqliteChatHistoryProvider Provider { get; }

		public ScriptedChatClient Client { get; }

		public ChatClientAgent Agent { get; }

		/// <summary>One turn of a conversation, as the execution runs it: a session named for the conversation.</summary>
		public async Task TurnAsync(String conversationId, String question)
		{
			AgentSession session = await this.Agent.CreateSessionAsync(CancellationToken.None);
			SqliteChatHistoryProvider.Remember(session, conversationId);

			await this.Agent.RunAsync([new ChatMessage(ChatRole.User, question)], session, cancellationToken: CancellationToken.None);
		}

		/// <summary>Stores messages as a run would, without going through a model.</summary>
		public Task StoreAsync(String conversationId, IReadOnlyList<ChatMessage> messages)
			=> this._store.AppendMessagesAsync(
				conversationId,
				"solo",
				[.. messages.Select(static message => JsonSerializer.Serialize(message, AIJsonUtilities.DefaultOptions))],
				CancellationToken.None);

		/// <summary>What the provider would hand the model for this conversation.</summary>
		public async Task<IReadOnlyList<ChatMessage>> ProvideAsync(String conversationId)
		{
			AgentSession session = await this.Agent.CreateSessionAsync(CancellationToken.None);
			SqliteChatHistoryProvider.Remember(session, conversationId);

			await this.Agent.RunAsync([new ChatMessage(ChatRole.User, "next")], session, cancellationToken: CancellationToken.None);

			// The client was sent the history and then the new message; the history is what came back.
			return [.. this.Client.Calls[^1].Messages.Take(this.Client.Calls[^1].Messages.Count - 1)];
		}

		public void Dispose()
		{
			this.Client.Dispose();
			this._folder?.Dispose();
		}
	}
}
