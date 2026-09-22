using System.Runtime.CompilerServices;
using FluentAssertions;
using FolderAssistant.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FolderAssistant.Tests.Agents;

/// <summary>
/// The facade a front end talks to (SPEC-100): the conversation is the one the caller names or a new one,
/// every response and update says which, and the messages reach the execution as they were given.
/// </summary>
public sealed class WorkflowRunnerTests
{
	[Fact]
	public async Task A_Call_That_Names_No_Conversation_Starts_One_And_Says_Which()
	{
		RecordingExecution execution = new();
		using WorkflowRunner runner = new(execution);

		ChatResponse first = await runner.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
		ChatResponse second = await runner.GetResponseAsync([new ChatMessage(ChatRole.User, "again")]);

		first.Text.Should().Be("answer to hello");
		first.ConversationId.Should().NotBeNullOrWhiteSpace().And.Be(execution.Conversations[0]);
		second.ConversationId.Should().NotBe(first.ConversationId);
	}

	[Fact]
	public async Task A_Named_Conversation_Is_The_One_The_Turn_Runs_In()
	{
		RecordingExecution execution = new();
		using WorkflowRunner runner = new(execution);

		ChatResponse response = await runner.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], new ChatOptions { ConversationId = "c7" });

		execution.Conversations.Should().Equal("c7");
		response.ConversationId.Should().Be("c7");
	}

	[Fact]
	public async Task Every_Streamed_Update_Says_Which_Conversation_It_Belongs_To()
	{
		RecordingExecution execution = new();
		using WorkflowRunner runner = new(execution);

		List<ChatResponseUpdate> updates = await runner.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")], new ChatOptions { ConversationId = "c7" }).ToListAsync();

		updates.Select(update => update.Text).Should().Equal("answer to ", "hello");
		updates.Should().OnlyContain(update => update.ConversationId == "c7");
		execution.Conversations.Should().Equal("c7");
	}

	[Fact]
	public void It_Answers_For_Itself_And_For_Nothing_Keyed()
	{
		using WorkflowRunner runner = new(new RecordingExecution());

		runner.GetService(typeof(WorkflowRunner)).Should().BeSameAs(runner);
		runner.GetService(typeof(IChatClient)).Should().BeSameAs(runner);
		runner.GetService(typeof(WorkflowRunner), "key").Should().BeNull();
		runner.GetService(typeof(String)).Should().BeNull();
	}

	private sealed class RecordingExecution : IAgentExecution
	{
		public List<String> Conversations { get; } = [];

		public Task<AgentResponse> RunAsync(String conversationId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
		{
			this.Conversations.Add(conversationId);

			return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, $"answer to {messages[^1].Text}")));
		}

		public async IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(String conversationId, IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken cancellationToken)
		{
			this.Conversations.Add(conversationId);
			await Task.Yield();

			yield return new AgentResponseUpdate(ChatRole.Assistant, "answer to ");
			yield return new AgentResponseUpdate(ChatRole.Assistant, messages[^1].Text);
		}
	}
}
