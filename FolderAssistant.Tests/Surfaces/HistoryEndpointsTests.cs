using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FolderAssistant.Agents;
using FolderAssistant.Indexing;
using FolderAssistant.Persistence;
using FolderAssistant.Surfaces;
using FolderAssistant.Tests.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace FolderAssistant.Tests.Surfaces;

/// <summary>
/// Reading a conversation back, through the running host (SPEC-170). These go through the real root
/// because the subject is the endpoint over the real store: a test calling the store directly would
/// assert the SQL and say nothing about what an operator can see.
/// </summary>
public sealed class HistoryEndpointsTests
{
	private static readonly (String Key, String Value) InProcessProfile = ($"{AgentConfig.SectionName}:Profile", "lsa-blob");

	[Fact]
	public async Task A_Conversation_Is_Read_Back_In_Order_With_Its_Question_And_Answer()
	{
		using TempFolder folder = new();
		ScriptedChatClient model = new(ScriptedChatClient.Text("a list"));
		using HostFixture host = new(folder, InProcessProfile)
		{
			TestServices = services => services.AddSingleton<Func<ProviderConfig, IChatClient>>(_ => _ => model),
		};

		using HttpClient client = host.CreateClient();
		await client.GetAsync("/");
		WorkflowRunner runner = host.Services.GetRequiredService<WorkflowRunner>();
		ChatResponse turn = await runner.GetResponseAsync([new ChatMessage(ChatRole.User, "what is in the notes?")]);

		HistoryResponseDto? history = await client
			.GetFromJsonAsync<HistoryResponseDto>($"/api/history?conversation={turn.ConversationId}");

		history.Should().NotBeNull();
		history!.ConversationId.Should().Be(turn.ConversationId);
		history.Messages.Should().Be(2);
		history.OmittedFromStart.Should().Be(0);
		history.Window.Select(static message => (message.Role, message.Text))
			.Should().Equal(("user", "what is in the notes?"), ("assistant", "a list"));
		history.Window.Should().OnlyContain(message => message.Agent == "Folder Assistant");
	}

	/// <summary>
	/// A tool call and its result are messages, and a reader is shown both: the tool's name with its
	/// arguments, and what it returned. A projection that rendered only text would show a conversation in
	/// which the model answered out of nowhere.
	/// </summary>
	[Fact]
	public async Task A_Tool_Call_And_Its_Result_Are_Shown()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("notes.md"), "alpha beta gamma delta epsilon zeta");
		ScriptedChatClient model = new(
			ScriptedChatClient.Call("SearchIndex", new() { ["query"] = "alpha beta", ["maxResults"] = 3 }),
			ScriptedChatClient.Text("Found it."));
		using HostFixture host = new(folder, InProcessProfile)
		{
			TestServices = services => services.AddSingleton<Func<ProviderConfig, IChatClient>>(_ => _ => model),
		};

		using HttpClient client = host.CreateClient();
		await client.GetAsync("/");
		IIndexState state = host.Services.GetRequiredService<IIndexState>();
		await WaitFor(() => state.Status == IndexStatus.Ready);
		WorkflowRunner runner = host.Services.GetRequiredService<WorkflowRunner>();
		ChatResponse turn = await runner.GetResponseAsync([new ChatMessage(ChatRole.User, "what is in the notes?")]);

		HistoryResponseDto? history = await client
			.GetFromJsonAsync<HistoryResponseDto>($"/api/history?conversation={turn.ConversationId}");

		history!.Window.SelectMany(static message => message.ToolCalls).Select(static call => call.Tool)
			.Should().Equal("SearchIndex");
		history.Window.SelectMany(static message => message.ToolCalls).Single().Arguments
			.Should().Contain("query").And.Contain("alpha beta");
		history.Window.SelectMany(static message => message.ToolResults).Should().ContainSingle()
			.Which.Should().Contain("notes.md", "a tool result is the folder's own content, shown to the folder's owner");
	}

	[Fact]
	public async Task The_Conversations_Are_Listed_Newest_First()
	{
		using TempFolder folder = new();
		ScriptedChatClient model = new(ScriptedChatClient.Text("one"), ScriptedChatClient.Text("two"));
		using HostFixture host = new(folder, InProcessProfile)
		{
			TestServices = services => services.AddSingleton<Func<ProviderConfig, IChatClient>>(_ => _ => model),
		};

		using HttpClient client = host.CreateClient();
		await client.GetAsync("/");
		WorkflowRunner runner = host.Services.GetRequiredService<WorkflowRunner>();
		ChatResponse first = await runner.GetResponseAsync([new ChatMessage(ChatRole.User, "first")]);
		ChatResponse second = await runner.GetResponseAsync([new ChatMessage(ChatRole.User, "second")]);

		HistoryListDto? list = await client.GetFromJsonAsync<HistoryListDto>("/api/history");

		list.Should().NotBeNull();
		list!.Truncated.Should().BeFalse();
		list.Conversations.Select(static conversation => conversation.ConversationId)
			.Should().Contain([first.ConversationId!, second.ConversationId!]);
		list.Conversations.Should().OnlyContain(conversation => conversation.Messages == 2);
	}

	/// <summary>
	/// A name nobody started is 404, not an empty transcript. Shown an empty one, a reader would believe
	/// the name and conclude the conversation had nothing in it.
	/// </summary>
	[Fact]
	public async Task An_Unknown_Conversation_Is_Not_Found()
	{
		using TempFolder folder = new();
		using HostFixture host = new(folder);

		using HttpClient client = host.CreateClient();

		(await client.GetAsync("/api/history?conversation=never-happened")).StatusCode
			.Should().Be(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Deleting_A_Conversation_Removes_It_And_Its_Session()
	{
		using TempFolder folder = new();
		ScriptedChatClient model = new(ScriptedChatClient.Text("a list"));
		using HostFixture host = new(folder, InProcessProfile)
		{
			TestServices = services => services.AddSingleton<Func<ProviderConfig, IChatClient>>(_ => _ => model),
		};

		using HttpClient client = host.CreateClient();
		await client.GetAsync("/");
		WorkflowRunner runner = host.Services.GetRequiredService<WorkflowRunner>();
		ChatResponse turn = await runner.GetResponseAsync([new ChatMessage(ChatRole.User, "what is in the notes?")]);

		(await client.DeleteAsync($"/api/history?conversation={turn.ConversationId}")).StatusCode
			.Should().Be(HttpStatusCode.NoContent);

		(await client.GetAsync($"/api/history?conversation={turn.ConversationId}")).StatusCode
			.Should().Be(HttpStatusCode.NotFound);

		ConversationStore store = host.Services.GetRequiredService<ConversationStore>();
		(await store.LoadAsync("Folder Assistant", turn.ConversationId!, CancellationToken.None))
			.Should().BeNull("the cascade takes the session with the conversation");
	}

	/// <summary>
	/// A delete with no conversation named is refused rather than read as "all of them". There is no undo,
	/// and one forgotten parameter should not be able to clear every conversation on the machine.
	/// </summary>
	[Fact]
	public async Task Deleting_Without_Naming_A_Conversation_Is_Refused()
	{
		using TempFolder folder = new();
		using HostFixture host = new(folder);

		using HttpClient client = host.CreateClient();

		(await client.DeleteAsync("/api/history")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
	}

	/// <summary>
	/// Deleting a conversation nobody started is 404 and not a silent success, so a script that deletes
	/// the wrong name is told.
	/// </summary>
	[Fact]
	public async Task Deleting_An_Unknown_Conversation_Is_Not_Found()
	{
		using TempFolder folder = new();
		using HostFixture host = new(folder);

		using HttpClient client = host.CreateClient();

		(await client.DeleteAsync("/api/history?conversation=never-happened")).StatusCode
			.Should().Be(HttpStatusCode.NotFound);
	}

	/// <summary>
	/// A conversation longer than the window yields its end and says how many earlier messages it left
	/// out. A window taken for the whole is how a reader concludes a turn never happened.
	/// </summary>
	[Fact]
	public async Task A_Long_Conversation_Yields_Its_End_And_Says_What_It_Left_Out()
	{
		using TempFolder folder = new();
		using HostFixture host = new(folder);

		using HttpClient client = host.CreateClient();
		await client.GetAsync("/");
		ConversationStore store = host.Services.GetRequiredService<ConversationStore>();

		Int32 count = HistoryEndpoints.MaxMessages + 5;
		await store.AppendMessagesAsync(
			"c1",
			"solo",
			[.. Enumerable.Range(1, count).Select(static number =>
				JsonSerializer.Serialize(new ChatMessage(ChatRole.User, $"message {number}"), AIJsonUtilities.DefaultOptions))],
			CancellationToken.None);

		HistoryResponseDto? history = await client.GetFromJsonAsync<HistoryResponseDto>("/api/history?conversation=c1");

		history!.Messages.Should().Be(count);
		history.Window.Should().HaveCount(HistoryEndpoints.MaxMessages);
		history.OmittedFromStart.Should().Be(5);
		history.Window[^1].Text.Should().Be($"message {count}", "the window is the end of the conversation");
	}

	/// <summary>
	/// A row this reader cannot parse is reported as itself. A turn refuses to continue such a
	/// conversation, and a listing that threw would leave nobody able to see which message is the problem.
	/// </summary>
	[Fact]
	public async Task An_Unreadable_Message_Is_Reported_Rather_Than_Hidden()
	{
		using TempFolder folder = new();
		using HostFixture host = new(folder);

		using HttpClient client = host.CreateClient();
		await client.GetAsync("/");
		ConversationStore store = host.Services.GetRequiredService<ConversationStore>();

		await store.AppendMessagesAsync("c1", "solo", ["{\"role\":"], CancellationToken.None);

		HistoryResponseDto? history = await client.GetFromJsonAsync<HistoryResponseDto>("/api/history?conversation=c1");

		history!.Window.Should().ContainSingle().Which.Role.Should().Be("unreadable");
	}

	private static async Task WaitFor(Func<Boolean> condition)
	{
		DateTime deadline = DateTime.UtcNow.AddSeconds(15);
		while (!condition() && DateTime.UtcNow < deadline)
		{
			await Task.Delay(25);
		}

		condition().Should().BeTrue("the index should have become ready");
	}

	/// <summary>The response as a reader receives it, named as the JSON is rather than as the record is.</summary>
	private sealed record HistoryResponseDto(
		String ConversationId,
		String CreatedUtc,
		String UpdatedUtc,
		Int64 Messages,
		Int64 OmittedFromStart,
		IReadOnlyList<HistoryMessageDto> Window);

	private sealed record HistoryMessageDto(
		Int64 Seq,
		String Role,
		String Agent,
		String CreatedUtc,
		String? Text,
		IReadOnlyList<HistoryToolCallDto> ToolCalls,
		IReadOnlyList<String> ToolResults,
		Boolean Truncated);

	private sealed record HistoryToolCallDto(String Tool, String? Arguments);

	private sealed record HistoryListDto(IReadOnlyList<ConversationSummaryDto> Conversations, Boolean Truncated);

	private sealed record ConversationSummaryDto(String ConversationId, String CreatedUtc, String UpdatedUtc, Int64 Messages);
}
