using FluentAssertions;
using FolderAssistant.Persistence;
using Microsoft.Data.Sqlite;

namespace FolderAssistant.Tests;

/// <summary>
/// The conversation database and the rules that make it a second file rather than a second table set
/// (<c>SPEC-170</c>). The writers it is shaped for are the framework's history provider, which stores a
/// turn's messages as the framework serialized them, and the session store beside it.
/// </summary>
public sealed class ConversationDatabaseBootstrapperTests
{
	[Fact]
	public void It_Creates_The_Database_In_The_Metadata_Folder()
	{
		using TempFolder folder = new();

		ConversationDatabaseBootstrapResult result = BootstrapIn(folder.Path);

		result.DatabasePath.Should().Be(Path.Combine(folder.Path, ".folderassistant", "conversations.db"));
		File.Exists(result.DatabasePath).Should().BeTrue();
		result.Created.Should().BeTrue();
	}

	[Fact]
	public void Reusing_An_Existing_Database_Reports_That_It_Was_Not_Created()
	{
		using TempFolder folder = new();

		BootstrapIn(folder.Path);

		BootstrapIn(folder.Path).Created.Should().BeFalse();
	}

	/// <summary>
	/// Read on a connection that does not set it, which is what proves it was persisted into the file
	/// rather than applied per connection. It is what lets history be read while a turn writes.
	/// </summary>
	[Fact]
	public void The_Database_Is_In_Write_Ahead_Logging_Mode()
	{
		using TempFolder folder = new();

		ReadScalar(BootstrapIn(folder.Path).DatabasePath, "PRAGMA journal_mode;").Should().Be("wal");
	}

	[Fact]
	public void Every_Table_The_Store_Will_Write_Is_Created()
	{
		using TempFolder folder = new();

		ConversationDatabaseBootstrapResult result = BootstrapIn(folder.Path);

		ObjectNames(result.DatabasePath, "table").Should().Contain([
			"schema_version",
			"conversation",
			"message",
			"session_state",
		]);

		ObjectNames(result.DatabasePath, "index").Should().Contain("idx_conversation_updated");
	}

	/// <summary>
	/// The columns each writer depends on, asserted by writing through them: the history provider's
	/// messages under allocated sequences, each naming the agent that produced it and holding the
	/// framework's serialized message rather than display text, and a session keyed by agent and
	/// conversation.
	/// </summary>
	[Fact]
	public void A_Conversation_A_Message_And_A_Session_Can_Be_Written()
	{
		using TempFolder folder = new();

		using SqliteConnection connection = Connect(BootstrapIn(folder.Path).DatabasePath);
		SeedOneConversation(connection);

		Execute(connection, """
			INSERT INTO message (conversation_id, seq, agent_name, message_json, created_utc)
			VALUES ('c1', 1, 'orchestrator', '{"role":"user","contents":[{"$type":"text","text":"tides?"}]}',
			        '2026-01-01T00:00:00Z'),
			       ('c1', 2, 'orchestrator', '{"role":"assistant","contents":[{"$type":"functionCall"}]}',
			        '2026-01-01T00:00:01Z');

			INSERT INTO session_state (agent_name, conversation_id, session_json, updated_utc)
			VALUES ('orchestrator', 'c1', '{}', '2026-01-01T00:00:01Z'),
			       ('reader', 'c1', '{}', '2026-01-01T00:00:01Z');
			""");

		Scalar(connection, "SELECT COUNT(*) FROM message WHERE conversation_id = 'c1';").Should().Be(2L,
			"a turn stores its request and response messages together");
		Scalar(connection, "SELECT COUNT(*) FROM session_state WHERE conversation_id = 'c1';").Should().Be(2L,
			"two agents serving one conversation hold two sessions");
	}

	/// <summary>
	/// The sequence allocator exists from the first row, which is why the schema is created whole: a
	/// counter added later could only be reconstructed from <c>MAX(seq)</c>, the thing it exists to avoid.
	/// A turn reserves as many numbers as it is about to write, since the provider stores a run's request
	/// and response messages together.
	/// </summary>
	[Fact]
	public void A_Message_Sequence_Is_Allocated_By_Advancing_The_Conversations_Counter()
	{
		using TempFolder folder = new();

		using SqliteConnection connection = Connect(BootstrapIn(folder.Path).DatabasePath);
		SeedOneConversation(connection);

		Scalar(connection, """
			UPDATE conversation SET next_seq = next_seq + 1
			WHERE conversation_id = 'c1'
			RETURNING next_seq - 1;
			""").Should().Be(1L, "the first message takes sequence one");

		Scalar(connection, """
			UPDATE conversation SET next_seq = next_seq + 1
			WHERE conversation_id = 'c1'
			RETURNING next_seq - 1;
			""").Should().Be(2L, "the counter, not the rows, is what the next sequence comes from");

		// A turn writing three messages takes three numbers in one statement, and the first of them.
		Scalar(connection, """
			UPDATE conversation SET next_seq = next_seq + 3
			WHERE conversation_id = 'c1'
			RETURNING next_seq - 3;
			""").Should().Be(3L);

		Scalar(connection, "SELECT next_seq FROM conversation WHERE conversation_id = 'c1';").Should().Be(6L);
	}

	/// <summary>
	/// Two messages cannot claim one position. This is what fails loudly if anything ever allocates a
	/// sequence from <c>MAX(seq) + 1</c>, where two concurrent turns read the same value.
	/// </summary>
	[Fact]
	public void Two_Messages_Cannot_Take_The_Same_Sequence()
	{
		using TempFolder folder = new();

		using SqliteConnection connection = Connect(BootstrapIn(folder.Path).DatabasePath);
		SeedOneConversation(connection);

		Execute(connection, """
			INSERT INTO message (conversation_id, seq, agent_name, message_json, created_utc)
			VALUES ('c1', 1, 'orchestrator', '{}', '2026-01-01T00:00:00Z');
			""");

		FluentActions.Invoking(() => Execute(connection, """
				INSERT INTO message (conversation_id, seq, agent_name, message_json, created_utc)
				VALUES ('c1', 1, 'orchestrator', '{}', '2026-01-01T00:00:00Z');
				"""))
			.Should().Throw<SqliteException>();
	}

	/// <summary>
	/// Deleting a conversation takes its messages and sessions with it, so a history endpoint's delete
	/// is one statement rather than three that can half-succeed. Foreign keys are per connection, so
	/// this enables them on its own; what it pins is that the cascade is declared.
	/// </summary>
	[Fact]
	public void Deleting_A_Conversation_Cascades_To_Its_Messages_And_Sessions()
	{
		using TempFolder folder = new();

		using SqliteConnection connection = Connect(BootstrapIn(folder.Path).DatabasePath);
		SeedOneConversation(connection);

		Execute(connection, """
			INSERT INTO message (conversation_id, seq, agent_name, message_json, created_utc)
			VALUES ('c1', 1, 'orchestrator', '{}', '2026-01-01T00:00:00Z');

			INSERT INTO session_state (agent_name, conversation_id, session_json, updated_utc)
			VALUES ('orchestrator', 'c1', '{}', '2026-01-01T00:00:00Z');
			""");

		Execute(connection, "DELETE FROM conversation WHERE conversation_id = 'c1';");

		Scalar(connection, "SELECT COUNT(*) FROM message;").Should().Be(0L);
		Scalar(connection, "SELECT COUNT(*) FROM session_state;").Should().Be(0L);
	}

	/// <summary>
	/// The property the second file exists for, asserted as that property rather than as two paths
	/// being different — two paths compared would pass while both pointed at one file through
	/// different configuration.
	///
	/// <para>
	/// The recovery this project tells an operator to reach for is deleting the index and running
	/// again. A conversation is not derived from the folder, so it has to be there afterwards.
	/// </para>
	/// </summary>
	[Fact]
	public void A_Conversation_Survives_The_Index_Database_Being_Deleted_And_Rebuilt()
	{
		using TempFolder folder = new();

		String indexPath = new FolderDatabaseBootstrapper()
			.EnsureInitialized(folder.Path, new PersistenceConfig()).DatabasePath;

		String conversationPath = BootstrapIn(folder.Path).DatabasePath;

		using (SqliteConnection connection = Connect(conversationPath))
		{
			SeedOneConversation(connection);
		}

		// As an operator recovering from a corrupt index would: the index file goes, and the next run
		// builds it again. Its write-ahead log goes with it, or the rebuild reads the old pages back.
		foreach (String path in Directory.GetFiles(Path.GetDirectoryName(indexPath)!, "manifest.db*"))
		{
			File.Delete(path);
		}

		new FolderDatabaseBootstrapper().EnsureInitialized(folder.Path, new PersistenceConfig());

		ReadScalar(conversationPath, "SELECT COUNT(*) FROM conversation WHERE conversation_id = 'c1';")
			.Should().Be(1L, "a conversation is not derived from the folder and cannot be rebuilt from it");
	}

	/// <summary>
	/// Configuration is the one thing that can defeat the split, so it is refused where it is stated
	/// rather than at the first write — where the symptom would be conversations vanishing with an
	/// index rebuild, months later.
	/// </summary>
	[Fact]
	public void Naming_The_Index_Database_As_The_Conversation_Database_Is_Refused()
	{
		using TempFolder folder = new();

		FluentActions.Invoking(() => BootstrapIn(folder.Path, conversationDatabaseFileName: "manifest.db"))
			.Should().Throw<ArgumentException>()
			.WithMessage("*index database*");
	}

	[Fact]
	public void A_Configured_Metadata_Folder_Name_Is_Honoured()
	{
		using TempFolder folder = new();

		BootstrapIn(folder.Path, metadataFolderName: "_custom").DatabasePath
			.Should().Be(Path.Combine(folder.Path, "_custom", "conversations.db"));
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void A_Blank_Analyzed_Folder_Is_Refused(String path)
	{
		FluentActions
			.Invoking(() => new ConversationDatabaseBootstrapper().EnsureInitialized(path, new PersistenceConfig()))
			.Should().Throw<ArgumentException>();
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void A_Blank_Metadata_Folder_Name_Is_Refused(String name)
	{
		using TempFolder folder = new();

		FluentActions.Invoking(() => BootstrapIn(folder.Path, metadataFolderName: name))
			.Should().Throw<ArgumentException>();
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void A_Blank_Conversation_Database_File_Name_Is_Refused(String name)
	{
		using TempFolder folder = new();

		FluentActions.Invoking(() => BootstrapIn(folder.Path, conversationDatabaseFileName: name))
			.Should().Throw<ArgumentException>();
	}

	private static ConversationDatabaseBootstrapResult BootstrapIn(
		String analyzedFolder,
		String? metadataFolderName = null,
		String? conversationDatabaseFileName = null)
	{
		PersistenceConfig config = new();

		if (metadataFolderName is not null)
		{
			config = config with { MetadataFolderName = metadataFolderName };
		}

		if (conversationDatabaseFileName is not null)
		{
			config = config with { ConversationDatabaseFileName = conversationDatabaseFileName };
		}

		return new ConversationDatabaseBootstrapper().EnsureInitialized(analyzedFolder, config);
	}

	private static SqliteConnection Connect(String databasePath)
	{
		SqliteConnection connection = new($"Data Source={databasePath}");
		connection.Open();

		using SqliteCommand pragma = connection.CreateCommand();
		pragma.CommandText = "PRAGMA foreign_keys = ON;";
		pragma.ExecuteNonQuery();

		return connection;
	}

	private static void SeedOneConversation(SqliteConnection connection)
		=> Execute(connection, """
			INSERT INTO conversation (conversation_id, created_utc, updated_utc)
			VALUES ('c1', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
			""");

	private static void Execute(SqliteConnection connection, String sql)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = sql;
		command.ExecuteNonQuery();
	}

	private static Object Scalar(SqliteConnection connection, String sql)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = sql;

		return command.ExecuteScalar()!;
	}

	private static Object ReadScalar(String databasePath, String sql)
	{
		using SqliteConnection connection = Connect(databasePath);

		return Scalar(connection, sql);
	}

	private static List<String> ObjectNames(String databasePath, String type)
	{
		using SqliteConnection connection = Connect(databasePath);
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type;";
		command.Parameters.AddWithValue("$type", type);

		List<String> names = [];

		using SqliteDataReader reader = command.ExecuteReader();

		while (reader.Read())
		{
			names.Add(reader.GetString(0));
		}

		return names;
	}
}
