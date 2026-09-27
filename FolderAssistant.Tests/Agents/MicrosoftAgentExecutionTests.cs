using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using FolderAssistant.Agents;
using FolderAssistant.Retrieval;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FolderAssistant.Tests.Agents;

/// <summary>
/// The turn (SPEC-100), through a real agent over a client the test scripts: the session carried from one
/// turn of a conversation to the next and to no other conversation; a failed turn saving nothing; and the
/// turn's telemetry recorded inside the execution, which is the only place a streamed failure — drained
/// as text, the stream ending cleanly — can be told from a success.
/// </summary>
public sealed class MicrosoftAgentExecutionTests
{
	private static readonly IReadOnlyList<ChatMessage> Question = [new(ChatRole.User, "what is in notes.md?")];

	[Fact]
	public async Task A_Turn_Is_Recorded_And_The_Next_Turn_Of_The_Conversation_Remembers_It()
	{
		ScriptedChatClient client = new(ScriptedChatClient.Text("a list"), ScriptedChatClient.Text("three items"), ScriptedChatClient.Text("nothing yet"));
		using Fixture fixture = new(client);

		AgentResponse first = await fixture.Execution.RunAsync("c1", Question, CancellationToken.None);
		await fixture.Execution.RunAsync("c1", [new(ChatRole.User, "how long is it?")], CancellationToken.None);
		await fixture.Execution.RunAsync("c2", [new(ChatRole.User, "and you?")], CancellationToken.None);

		first.Text.Should().Be("a list");
		client.Calls[1].Messages.Select(message => message.Text).Should().Equal("what is in notes.md?", "a list", "how long is it?");
		client.Calls[2].Messages.Select(message => message.Text).Should().Equal("and you?");
		fixture.Telemetry.Turns.Should().HaveCount(3).And.OnlyContain(turn => turn.Status == TurnStatus.Success && !turn.Streamed && turn.Agent == "solo" && turn.ErrorCode == null);
	}

	[Fact]
	public async Task A_Failed_Turn_Is_The_Exception_Itself_Recorded_Failed_And_Saves_Nothing()
	{
		InvalidOperationException fault = new("the provider said no");
		using Fixture fixture = new(new FaultingChatClient(fault));

		Func<Task> turn = () => fixture.Execution.RunAsync("c1", Question, CancellationToken.None);

		(await turn.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(fault);
		fixture.Telemetry.Turns.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Status = TurnStatus.Failed, Streamed = false, ErrorCode = nameof(InvalidOperationException) });
		(await fixture.Sessions.LoadAsync("solo", "c1", CancellationToken.None)).Should().BeNull();
	}

	/// <summary>
	/// The test a decorator-based recording fails: the stream yields what it had, then the failure as text,
	/// and ends without throwing — so anything watching from outside sees a stream that completed.
	/// </summary>
	[Fact]
	public async Task A_Failed_Streamed_Turn_Drains_As_Text_And_Is_Recorded_Failed()
	{
		using Fixture fixture = new(new FaultingChatClient(new InvalidOperationException("the provider hung up"), "The file holds"));

		List<String> texts = [];
		await foreach (AgentResponseUpdate update in fixture.Execution.RunStreamingAsync("c1", Question, CancellationToken.None))
		{
			texts.Add(update.Text);
		}

		texts.Should().Equal("The file holds", MicrosoftAgentExecution.FailurePrefix + "the provider hung up");
		fixture.Telemetry.Turns.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Status = TurnStatus.Failed, Streamed = true, ErrorCode = nameof(InvalidOperationException) });
		(await fixture.Sessions.LoadAsync("solo", "c1", CancellationToken.None)).Should().BeNull();
	}

	[Fact]
	public async Task A_Streamed_Turn_Is_Recorded_Once_It_Has_Drained_And_Its_Session_Is_Saved()
	{
		using Fixture fixture = new(new ScriptedChatClient(ScriptedChatClient.Text("a list")));

		List<String> texts = [];
		await foreach (AgentResponseUpdate update in fixture.Execution.RunStreamingAsync("c1", Question, CancellationToken.None))
		{
			texts.Add(update.Text);
		}

		String.Concat(texts).Should().Be("a list");
		fixture.Telemetry.Turns.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Status = TurnStatus.Success, Streamed = true });
		(await fixture.Sessions.LoadAsync("solo", "c1", CancellationToken.None)).Should().NotBeNull();
	}

	[Fact]
	public async Task A_Stream_The_Caller_Stops_Reading_Is_Cancelled_Not_Successful()
	{
		using Fixture fixture = new(new FaultingChatClient(null, "one", "two", "three"));

		await using (IAsyncEnumerator<AgentResponseUpdate> updates = fixture.Execution.RunStreamingAsync("c1", Question, CancellationToken.None).GetAsyncEnumerator())
		{
			(await updates.MoveNextAsync()).Should().BeTrue();
		}

		fixture.Telemetry.Turns.Should().ContainSingle().Which.Status.Should().Be(TurnStatus.Cancelled);
		(await fixture.Sessions.LoadAsync("solo", "c1", CancellationToken.None)).Should().BeNull();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task A_Cancellation_Of_The_Callers_Token_Throws_And_Is_Recorded_Cancelled(Boolean streamed)
	{
		using CancellationTokenSource caller = new();
		using Fixture fixture = new(new FaultingChatClient(new OperationCanceledException(), onCall: caller.Cancel));

		Func<Task> turn = streamed
			? async () => await fixture.Execution.RunStreamingAsync("c1", Question, caller.Token).ToListAsync(caller.Token)
			: () => fixture.Execution.RunAsync("c1", Question, caller.Token);

		await turn.Should().ThrowAsync<OperationCanceledException>();
		fixture.Telemetry.Turns.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Status = TurnStatus.Cancelled, Streamed = streamed });
	}

	/// <summary>The token decides, not the type: a cancellation the caller never asked for is somebody's deadline.</summary>
	[Fact]
	public async Task A_Cancellation_Nobody_Asked_For_Is_A_Timeout_And_Streams_As_A_Failure()
	{
		using Fixture fixture = new(new FaultingChatClient(new TaskCanceledException("the transport gave up")));

		List<AgentResponseUpdate> updates = await fixture.Execution.RunStreamingAsync("c1", Question, CancellationToken.None).ToListAsync();

		// The note is the describer's sentence, not the SDK's message: a deadline is the provider's failure.
		updates.Should().ContainSingle().Which.Text.Should().StartWith(MicrosoftAgentExecution.FailurePrefix + "The provider did not answer within").And.NotContain("the transport gave up");
		fixture.Telemetry.Turns.Should().ContainSingle().Which.Status.Should().Be(TurnStatus.TimedOut);
	}

	[Fact]
	public void A_Fault_Is_Filed_By_The_Callers_Token_First_And_The_Type_Second()
	{
		using CancellationTokenSource cancelled = new();
		cancelled.Cancel();

		MicrosoftAgentExecution.Classify(new OperationCanceledException(), cancelled.Token).Should().Be(TurnStatus.Cancelled);
		MicrosoftAgentExecution.Classify(new OperationCanceledException(), CancellationToken.None).Should().Be(TurnStatus.TimedOut);
		MicrosoftAgentExecution.Classify(new TimeoutException(), CancellationToken.None).Should().Be(TurnStatus.TimedOut);
		MicrosoftAgentExecution.Classify(new InvalidOperationException(), cancelled.Token).Should().Be(TurnStatus.Failed);
		MicrosoftAgentExecution.Classify(new IndexNotReadyException("building"), CancellationToken.None).Should().Be(TurnStatus.NotReady);
		MicrosoftAgentExecution.Classify(new IndexNotReadyException("failed", new IOException(), isBuildFailure: true), CancellationToken.None).Should().Be(TurnStatus.Failed);
	}

	[Fact]
	public async Task A_Session_That_Cannot_Be_Read_Starts_A_Fresh_One_And_Says_So()
	{
		ScriptedChatClient client = new(ScriptedChatClient.Text("a list"));
		using Fixture fixture = new(client);
		await fixture.Sessions.SaveAsync("solo", "c1", JsonSerializer.SerializeToElement(new[] { "not", "a", "session" }), CancellationToken.None);

		AgentResponse response = await fixture.Execution.RunAsync("c1", Question, CancellationToken.None);

		response.Text.Should().Be("a list");
		client.Calls[0].Messages.Select(message => message.Text).Should().Equal("what is in notes.md?");
		fixture.Logger.Lines.Should().ContainSingle(line => line.Level == LogLevel.Warning).Which.Message.Should().Contain("c1").And.Contain("fresh");
		fixture.Telemetry.Turns.Should().ContainSingle().Which.Status.Should().Be(TurnStatus.Success);
	}

	[Fact]
	public async Task The_Latency_Stops_Before_The_Session_Is_Saved()
	{
		SlowSessionStore sessions = new(TimeSpan.FromMilliseconds(600));
		using Fixture fixture = new(new ScriptedChatClient(ScriptedChatClient.Text("a list"), ScriptedChatClient.Text("again")), sessions);

		// The first turn pays for the framework's one-time initialisation; the second is the measurement.
		await fixture.Execution.RunAsync("warm", Question, CancellationToken.None);
		await fixture.Execution.RunAsync("c1", Question, CancellationToken.None);

		sessions.Saves.Should().Be(2);
		fixture.Telemetry.Turns[1].LatencyMs.Should().BeLessThan(400);
	}

	[Fact]
	public async Task A_Streamed_Answer_Whose_Session_Cannot_Be_Saved_Says_So_After_The_Answer()
	{
		using Fixture fixture = new(new ScriptedChatClient(ScriptedChatClient.Text("a list")), new SlowSessionStore(TimeSpan.Zero, new IOException("the disk is full")));

		List<AgentResponseUpdate> updates = await fixture.Execution.RunStreamingAsync("c1", Question, CancellationToken.None).ToListAsync();

		updates[^1].Text.Should().Be(MicrosoftAgentExecution.SaveFailurePrefix + "the disk is full");
		String.Concat(updates.SkipLast(1).Select(update => update.Text)).Should().Be("a list");
		fixture.Telemetry.Turns.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Status = TurnStatus.Failed, ErrorCode = nameof(IOException) });
	}

	[Fact]
	public async Task The_Store_Keeps_One_Session_Per_Agent_And_Conversation()
	{
		InMemoryAgentSessionStore store = new();
		await store.SaveAsync("reader", "c1", JsonSerializer.SerializeToElement("reader's"), CancellationToken.None);
		await store.SaveAsync("mutator", "c1", JsonSerializer.SerializeToElement("mutator's"), CancellationToken.None);

		(await store.LoadAsync("reader", "c1", CancellationToken.None))!.Value.GetString().Should().Be("reader's");
		(await store.LoadAsync("mutator", "c1", CancellationToken.None))!.Value.GetString().Should().Be("mutator's");
		(await store.LoadAsync("reader", "c2", CancellationToken.None)).Should().BeNull();
	}

	/// <summary>A roster of one agent, "solo", over the given client, with everything the execution records kept.</summary>
	private sealed class Fixture : IDisposable
	{
		private readonly AgentRegistry _registry;

		public Fixture(IChatClient client, IAgentSessionStore? sessions = null)
		{
			// An agent holding no tools, said through the entry form: the root configuration's grant is
			// the read-only tool list, and naming a tool this empty catalog lacks is refused at startup.
			Roster roster = Roster.Build(
				new AgentConfig { Workflow = new WorkflowConfig { Agents = [new AgentEntryConfig { Name = "solo", Description = "answers" }] } },
				[]);
			this._registry = new AgentRegistry(roster, new AgentToolCatalog([]), _ => client);
			this.Sessions = sessions ?? new InMemoryAgentSessionStore();
			this.Execution = new MicrosoftAgentExecution(new StaticWorkflowRoute(roster, this._registry), this.Sessions, this.Telemetry, new ProviderErrorDescriber(), new TypedLogger(this.Logger));
		}

		public MicrosoftAgentExecution Execution { get; }

		public IAgentSessionStore Sessions { get; }

		public RecordingTurnTelemetry Telemetry { get; } = new();

		public RecordingLogger Logger { get; } = new();

		public void Dispose() => this._registry.Dispose();
	}

	private sealed class TypedLogger(ILogger inner) : ILogger<MicrosoftAgentExecution>
	{
		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

		public Boolean IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, String> formatter)
			=> inner.Log(logLevel, eventId, state, exception, formatter);
	}

	/// <summary>Streams the given texts, then throws the fault when there is one; asked for a whole response, it throws at once.</summary>
	private sealed class FaultingChatClient(Exception? fault, params String[] texts) : IChatClient
	{
		private readonly Action? _onCall;

		public FaultingChatClient(Exception? fault, Action onCall)
			: this(fault)
		{
			this._onCall = onCall;
		}

		public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
		{
			this._onCall?.Invoke();

			return fault is null
				? Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, String.Concat(texts))))
				: Task.FromException<ChatResponse>(fault);
		}

		public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			this._onCall?.Invoke();
			foreach (String text in texts)
			{
				await Task.Yield();

				yield return new ChatResponseUpdate(ChatRole.Assistant, text);
			}

			if (fault is not null)
			{
				throw fault;
			}
		}

		public Object? GetService(Type serviceType, Object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

		public void Dispose()
		{
			// Holds nothing.
		}
	}

	private sealed class SlowSessionStore(TimeSpan delay, Exception? fault = null) : IAgentSessionStore
	{
		private readonly InMemoryAgentSessionStore _inner = new();

		public Int32 Saves { get; private set; }

		public Task<JsonElement?> LoadAsync(String agentName, String conversationId, CancellationToken cancellationToken)
			=> this._inner.LoadAsync(agentName, conversationId, cancellationToken);

		public async Task SaveAsync(String agentName, String conversationId, JsonElement session, CancellationToken cancellationToken)
		{
			await Task.Delay(delay, cancellationToken);
			if (fault is not null)
			{
				throw fault;
			}

			this.Saves++;
			await this._inner.SaveAsync(agentName, conversationId, session, cancellationToken);
		}
	}
}

/// <summary>A sink that keeps every turn it is given.</summary>
internal sealed class RecordingTurnTelemetry : ITurnTelemetry
{
	public List<TurnTelemetry> Turns { get; } = [];

	public void Record(TurnTelemetry turn) => this.Turns.Add(turn);
}
