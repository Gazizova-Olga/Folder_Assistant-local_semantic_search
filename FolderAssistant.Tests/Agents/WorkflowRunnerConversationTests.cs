using FluentAssertions;
using FolderAssistant.Agents;
using Microsoft.Extensions.AI;

namespace FolderAssistant.Tests.Agents;

/// <summary>
/// The runner over the real execution and a real agent (SPEC-100): what the runner stamps on a streamed
/// update must not reach the framework's own bookkeeping. The runner names the conversation on every
/// update as it passes, and the object it was handed is one the agent framework and the tool-calling
/// loop are still accumulating; a conversation id written into it was read back as a server-managed
/// conversation, after which the loop sent a tool result without the call it answered and the next turn
/// nothing of this one. Found on the first real question, 2026-09-24: every streamed conversation forgot
/// every earlier turn.
/// </summary>
public sealed class WorkflowRunnerConversationTests
{
	private static readonly ChatMessage[] Question = [new(ChatRole.User, "what is in notes.md?")];

	[Fact]
	public async Task A_Streamed_Turn_Sends_A_Tool_Result_With_Its_Call_And_Is_Remembered_By_The_Next_Turn()
	{
		ScriptedChatClient client = new(
			ScriptedChatClient.Call("Echo", new() { ["text"] = "hi" }),
			ScriptedChatClient.Text("done"),
			ScriptedChatClient.Text("later"));
		AITool echo = AIFunctionFactory.Create((String text) => "echo:" + text, "Echo");
		// The entry form, not the root one: the root configuration's grant is the real read-only tool
		// list, which this stub catalog does not hold, and a grant naming a tool the catalog lacks is
		// refused at startup on purpose.
		Roster roster = Roster.Build(
			new AgentConfig { Workflow = new WorkflowConfig { Agents = [new AgentEntryConfig { Name = "solo", Description = "echoes", Tools = ["Echo"] }] } },
			["Echo"]);
		using AgentRegistry registry = new(roster, new AgentToolCatalog([echo]), _ => client);
		MicrosoftAgentExecution execution = new(new StaticWorkflowRoute(roster, registry), new InMemoryAgentSessionStore(), new RecordingTurnTelemetry(), new ProviderErrorDescriber());
		using WorkflowRunner runner = new(execution);

		List<ChatResponseUpdate> first = await runner.GetStreamingResponseAsync(Question, new ChatOptions { ConversationId = "c1" }).ToListAsync();
		List<ChatResponseUpdate> second = await runner.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "how long is it?")], new ChatOptions { ConversationId = "c1" }).ToListAsync();

		first.Should().OnlyContain(update => update.ConversationId == "c1");
		second.Should().OnlyContain(update => update.ConversationId == "c1");
		client.Calls.Should().HaveCount(3);

		// The loop's second call: the question, the call, and its result — not the result alone.
		client.Calls[1].Messages.Select(message => message.Role).Should().Equal(ChatRole.User, ChatRole.Assistant, ChatRole.Tool);
		client.Calls[1].Options?.ConversationId.Should().BeNull("the loop must not take the conversation for a server-managed one");

		// The next turn: the whole first turn, then the new question.
		List<String> texts = [.. client.Calls[2].Messages.Select(message => message.Text)];
		texts.Should().StartWith("what is in notes.md?");
		texts.Should().Contain("done");
		texts[^1].Should().Be("how long is it?");
		client.Calls[2].Options?.ConversationId.Should().BeNull();
	}
}
