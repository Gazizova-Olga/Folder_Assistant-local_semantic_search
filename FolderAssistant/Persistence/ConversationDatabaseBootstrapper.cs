using Microsoft.Data.Sqlite;

namespace FolderAssistant.Persistence;

/// <summary>
/// Where the conversation database is, and whether this call is what brought it into existence.
///
/// <para>
/// Its own type rather than <see cref="DatabaseBootstrapResult"/>, because both are registered as
/// singletons and a shared type would make one of them resolve the other — silently, and with every
/// conversation written into the index file.
/// </para>
/// </summary>
internal sealed record ConversationDatabaseBootstrapResult(String DatabasePath, Boolean Created);

/// <summary>Brings the conversation database into a usable state before anything reads from it.</summary>
internal interface IConversationDatabaseBootstrapper
{
	/// <summary>Ensures the metadata folder, the database and its schema exist. Idempotent.</summary>
	ConversationDatabaseBootstrapResult EnsureInitialized(String analyzedFolderPath, PersistenceConfig config);
}

/// <summary>
/// Creates the conversation database beside the index and ensures its schema on every run
/// (<c>SPEC-170</c>).
///
/// <para>
/// <strong>A second file, never the index one.</strong> An index rebuild drops and repopulates the index
/// schema, and the recovery this project tells an operator to reach for is deleting the metadata folder
/// and running again — in one file that would take every conversation with it, and a conversation is not
/// derived from the folder and cannot be rebuilt from anything. The access patterns are opposite as well:
/// the index has one writer in background bursts, while this is written on the request path once per turn,
/// and under one write lock the turn is the side that waits.
/// </para>
///
/// <para>
/// <strong>This type owns all of the DDL.</strong> No store creates a table, because schema creation from
/// the request path costs a round trip per turn and forces a read path onto a write-capable connection.
/// The composition root resolves this before the server listens, through the same startup filter as the
/// index bootstrap.
/// </para>
///
/// <para>
/// The schema is created whole rather than grown a table at a time, for the reason
/// <see cref="FolderDatabaseBootstrapper"/> is: one invariant here cannot be retrofitted.
/// <c>message.seq</c> is allocated by advancing <c>conversation.next_seq</c>, so the counter has to
/// exist from the first row written — a counter added later could only be reconstructed from
/// <c>MAX(seq)</c>, which is the thing it exists to avoid.
/// </para>
/// </summary>
internal sealed class ConversationDatabaseBootstrapper : IConversationDatabaseBootstrapper
{
	private const Int32 SchemaVersion = 1;

	/// <summary>Ensures the metadata folder, the database and its schema exist. Idempotent.</summary>
	public ConversationDatabaseBootstrapResult EnsureInitialized(String analyzedFolderPath, PersistenceConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);

		if (String.IsNullOrWhiteSpace(analyzedFolderPath))
		{
			throw new ArgumentException("Analyzed folder path must be provided.", nameof(analyzedFolderPath));
		}

		if (String.IsNullOrWhiteSpace(config.MetadataFolderName))
		{
			throw new ArgumentException("Metadata folder name must be provided.", nameof(config));
		}

		if (String.IsNullOrWhiteSpace(config.ConversationDatabaseFileName))
		{
			throw new ArgumentException("Conversation database file name must be provided.", nameof(config));
		}

		if (String.Equals(
			config.ConversationDatabaseFileName,
			config.DatabaseFileName,
			StringComparison.OrdinalIgnoreCase))
		{
			// The whole point of this type is that the two are separate files, and configuration is the
			// one place that can defeat it. Refused here rather than at the first write, where the
			// symptom would be conversations disappearing with an index rebuild.
			throw new ArgumentException(
				"The conversation database must not be the index database: "
					+ $"'{config.ConversationDatabaseFileName}' is configured for both. An index rebuild drops the "
					+ "index schema, and conversations have to survive it (SPEC-170).",
				nameof(config));
		}

		String rootPath = Path.GetFullPath(analyzedFolderPath);
		String metadataPath = Path.Combine(rootPath, config.MetadataFolderName);
		Directory.CreateDirectory(metadataPath);

		String databasePath = Path.Combine(metadataPath, config.ConversationDatabaseFileName);

		using SqliteConnection connection = FolderDatabaseConnection.OpenCreate(databasePath);

		using (SqliteCommand pragma = connection.CreateCommand())
		{
			// Persisted in the file, unlike the per-connection settings the factory applies, so it is
			// written once. It is what lets a front end read history while a turn writes.
			pragma.CommandText = "PRAGMA journal_mode = WAL;";
			pragma.ExecuteNonQuery();
		}

		using SqliteTransaction transaction = connection.BeginTransaction();

		using SqliteCommand schema = connection.CreateCommand();
		schema.Transaction = transaction;
		schema.CommandText = """
			CREATE TABLE IF NOT EXISTS schema_version (
				id      INTEGER PRIMARY KEY CHECK (id = 1),
				version INTEGER NOT NULL
			);

			-- next_seq is the allocator for this conversation's message numbers, advanced inside the
			-- transaction that writes a row — by as many as the write holds, since one turn stores its
			-- request and response messages together. MAX(seq) + 1 is what it exists to avoid: two turns
			-- of one conversation are not ordered against each other, so two writers reading the maximum
			-- see the same value and claim the same position.
			CREATE TABLE IF NOT EXISTS conversation (
				conversation_id TEXT PRIMARY KEY,
				created_utc     TEXT NOT NULL,
				updated_utc     TEXT NOT NULL,
				next_seq        INTEGER NOT NULL DEFAULT 1
			);

			-- message_json is the framework's own serialized ChatMessage, not display text. These rows are
			-- what the history provider hands back to the model on the next turn, so a row has to round-trip
			-- exactly what was said — a tool call, its result, a refusal — and text would silently drop all
			-- of it. A front end showing history projects these to text; the store never stores that
			-- projection, because two spellings of one message can disagree about what the model saw.
			--
			-- agent_name is whichever agent produced the message, from the provider's own invocation
			-- context: a delegating roster answers one conversation through more than one agent.
			CREATE TABLE IF NOT EXISTS message (
				conversation_id TEXT NOT NULL,
				seq             INTEGER NOT NULL,
				agent_name      TEXT NOT NULL,
				message_json    TEXT NOT NULL,
				created_utc     TEXT NOT NULL,
				PRIMARY KEY (conversation_id, seq),
				FOREIGN KEY (conversation_id) REFERENCES conversation(conversation_id) ON DELETE CASCADE
			);

			-- Keyed by agent and conversation: two agents serving one conversation hold two sessions,
			-- and a key of the conversation alone would have each overwrite the other's. The blob is the
			-- framework's own format, and once a history provider writes the messages it holds what is
			-- left of a session — routing, approvals, whatever a context provider keeps — and not the
			-- conversation. That is the point of the provider: one copy of the history, in rows.
			CREATE TABLE IF NOT EXISTS session_state (
				agent_name      TEXT NOT NULL,
				conversation_id TEXT NOT NULL,
				session_json    TEXT NOT NULL,
				updated_utc     TEXT NOT NULL,
				PRIMARY KEY (agent_name, conversation_id),
				FOREIGN KEY (conversation_id) REFERENCES conversation(conversation_id) ON DELETE CASCADE
			);

			-- History is read in order for one conversation, which the primary key already serves; this
			-- is for listing conversations by recency.
			CREATE INDEX IF NOT EXISTS idx_conversation_updated ON conversation(updated_utc);
			""";
		schema.ExecuteNonQuery();

		using SqliteCommand seed = connection.CreateCommand();
		seed.Transaction = transaction;
		seed.CommandText = $"""
			INSERT INTO schema_version (id, version)
			SELECT 1, {SchemaVersion}
			WHERE NOT EXISTS (SELECT 1 FROM schema_version WHERE id = 1);
			""";

		Boolean created = seed.ExecuteNonQuery() > 0;

		transaction.Commit();

		return new ConversationDatabaseBootstrapResult(databasePath, created);
	}
}
