using System.Text;
using System.Text.Json;
using FolderAssistant.Persistence;
using Microsoft.Extensions.AI;

namespace FolderAssistant.Surfaces;

/// <summary>One tool call a message made, as a reader sees it.</summary>
internal sealed record HistoryToolCall(String Tool, String? Arguments);

/// <summary>
/// One message, rendered for reading (<c>SPEC-170</c>).
///
/// <para>
/// This shape is **derived on read and never stored**. What the database holds is the framework's own
/// serialization, because those rows are what the model is given on the next turn; a second stored form
/// could disagree with it about what was said, and the disagreement would be invisible.
/// </para>
/// </summary>
internal sealed record HistoryMessage(
	Int64 Seq,
	String Role,
	String Agent,
	String CreatedUtc,
	String? Text,
	IReadOnlyList<HistoryToolCall> ToolCalls,
	IReadOnlyList<String> ToolResults,
	Boolean Truncated);

/// <summary>One conversation's transcript, and what was left out of it.</summary>
internal sealed record HistoryResponse(
	String ConversationId,
	String CreatedUtc,
	String UpdatedUtc,
	Int64 Messages,
	Int64 OmittedFromStart,
	IReadOnlyList<HistoryMessage> Window);

/// <summary>The conversations the database holds, newest first.</summary>
internal sealed record HistoryListResponse(IReadOnlyList<ConversationSummary> Conversations, Boolean Truncated);

/// <summary>
/// Reading and clearing what a conversation was (<c>SPEC-170</c>): the first reader of the rows the
/// history provider writes.
///
/// <para>
/// <strong>Every bound is a constant here and every cut is said in the response</strong>, the same rule the
/// file tools hold: a window silently taken for the whole is how a reader concludes a turn never happened.
/// A conversation yields its most recent messages, with the number of earlier ones it left out; a message's
/// text is bounded and flagged; the list of conversations says whether it is all of them.
/// </para>
///
/// <para>
/// <strong>A message is rendered, not re-serialized.</strong> The row holds the framework's form and stays
/// that way; this turns it into text, the tools it called and what they returned. A tool result is shown
/// because it is the folder's own content and the person reading owns the folder — but bounded, since a
/// passage search's result is the largest thing in a conversation.
/// </para>
/// </summary>
internal static class HistoryEndpoints
{
	/// <summary>How many conversations one listing returns.</summary>
	internal const Int32 MaxConversations = 200;

	/// <summary>How many messages of one conversation are returned, counted from its end.</summary>
	internal const Int32 MaxMessages = 200;

	/// <summary>How many characters of one rendered message are returned.</summary>
	internal const Int32 MaxCharactersPerMessage = 4000;

	public static void MapHistory(this WebApplication app)
	{
		ArgumentNullException.ThrowIfNull(app);

		// One route with an optional conversation, rather than a resource tree: a conversation is not a
		// thing this application creates on request, it is what a turn leaves behind.
		app.MapGet("/api/history", async (
			ConversationStore store,
			String? conversation,
			CancellationToken cancellationToken) =>
		{
			if (String.IsNullOrWhiteSpace(conversation))
			{
				ConversationPage page = await store.ListConversationsAsync(MaxConversations, cancellationToken);

				return Results.Ok(new HistoryListResponse(page.Conversations, page.Truncated));
			}

			ConversationTranscript? transcript = await store
				.ReadConversationAsync(conversation, MaxMessages, cancellationToken);

			// Absent and empty are different answers: a mistyped name that returned an empty transcript
			// would read as a conversation in which nothing was said.
			return transcript is null ? Results.NotFound() : Results.Ok(Render(transcript));
		});

		app.MapDelete("/api/history", async (
			ConversationStore store,
			String? conversation,
			CancellationToken cancellationToken) =>
		{
			if (String.IsNullOrWhiteSpace(conversation))
			{
				// Refused rather than taken as "all of them": a delete with a forgotten parameter would
				// otherwise clear every conversation on the machine, and there is no undo.
				return Results.BadRequest(new
				{
					error = "Name the conversation to delete: DELETE /api/history?conversation=<id>. "
						+ "Deleting every conversation is not something one absent parameter should do.",
				});
			}

			Boolean deleted = await store.DeleteConversationAsync(conversation, cancellationToken);

			return deleted ? Results.NoContent() : Results.NotFound();
		});
	}

	private static HistoryResponse Render(ConversationTranscript transcript)
		=> new(
			transcript.Conversation.ConversationId,
			transcript.Conversation.CreatedUtc,
			transcript.Conversation.UpdatedUtc,
			transcript.Conversation.Messages,
			transcript.OmittedFromStart,
			[.. transcript.Messages.Select(Render)]);

	private static HistoryMessage Render(StoredMessage stored)
	{
		ChatMessage? message = TryRead(stored.MessageJson);

		if (message is null)
		{
			// A row this reader cannot parse is reported as itself rather than dropped or thrown over. The
			// turn refuses to continue such a conversation (SPEC-170); a person looking at it is entitled
			// to see which message is the problem, and a listing that failed whole would hide it.
			return new HistoryMessage(
				stored.Seq, "unreadable", stored.AgentName, stored.CreatedUtc,
				Text: null, ToolCalls: [], ToolResults: [], Truncated: false);
		}

		(String? text, Boolean truncated) = Bound(message.Text);

		return new HistoryMessage(
			stored.Seq,
			message.Role.Value,
			stored.AgentName,
			stored.CreatedUtc,
			text,
			[.. message.Contents.OfType<FunctionCallContent>().Select(static call =>
				new HistoryToolCall(call.Name, Describe(call.Arguments)))],
			[.. message.Contents.OfType<FunctionResultContent>().Select(static result => Bound(result.Result?.ToString()).Text ?? String.Empty)],
			truncated);
	}

	private static ChatMessage? TryRead(String json)
	{
		try
		{
			return JsonSerializer.Deserialize<ChatMessage>(json, AIJsonUtilities.DefaultOptions);
		}
		catch (Exception exception) when (exception is JsonException or NotSupportedException)
		{
			return null;
		}
	}

	private static (String? Text, Boolean Truncated) Bound(String? text)
	{
		if (String.IsNullOrEmpty(text))
		{
			return (null, false);
		}

		return text.Length <= MaxCharactersPerMessage
			? (text, false)
			: (text[..MaxCharactersPerMessage], true);
	}

	/// <summary>
	/// A call's arguments as one line. The values come back as <c>JsonElement</c> — that is how the
	/// framework carries them — so they are written as JSON rather than through <c>ToString</c>, which
	/// would render a number and an object the same unhelpful way.
	/// </summary>
	private static String? Describe(IDictionary<String, Object?>? arguments)
	{
		if (arguments is null || arguments.Count == 0)
		{
			return null;
		}

		StringBuilder description = new();

		foreach ((String name, Object? value) in arguments)
		{
			if (description.Length > 0)
			{
				description.Append(", ");
			}

			description.Append(name).Append('=').Append(JsonSerializer.Serialize(value, AIJsonUtilities.DefaultOptions));
		}

		return Bound(description.ToString()).Text;
	}
}
