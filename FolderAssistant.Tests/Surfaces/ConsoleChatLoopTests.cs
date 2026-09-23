using System.Diagnostics;
using System.Runtime.CompilerServices;
using FluentAssertions;
using FolderAssistant.Agents;
using FolderAssistant.Surfaces;
using Microsoft.Extensions.AI;

namespace FolderAssistant.Tests.Surfaces;

/// <summary>
/// The console loop over a scripted reader, a writer that is read back, and a runner that streams what
/// it is told. What is checked is the loop's own decisions: what a line becomes, which conversation the
/// next one continues, what ends the loop and how, and that a failed turn is said rather than swallowed
/// or fatal.
/// </summary>
public sealed class ConsoleChatLoopTests
{
	private const String Folder = @"C:\some\folder";

	[Fact]
	public async Task A_Line_Is_One_User_Message_And_Its_Streamed_Answer_Is_Written_Then_Ended()
	{
		StreamingRunner runner = new("Three", " words.");
		StringWriter output = new();
		ConsoleChatLoop loop = new(() => runner, new ConsoleStreams(new StringReader("what is in notes.md?\n"), output, false), Folder);

		ConsoleLoopEnd end = await loop.RunAsync(CancellationToken.None);

		end.Should().Be(ConsoleLoopEnd.InputEnded);
		runner.Calls.Should().ContainSingle();
		runner.Calls[0].Messages.Should().ContainSingle().Which.Role.Should().Be(ChatRole.User);
		runner.Calls[0].Messages[0].Text.Should().Be("what is in notes.md?");
		output.ToString().Should().Be(
			ConsoleChatLoop.Banner(Folder) + Environment.NewLine
			+ ConsoleChatLoop.Prompt + "Three words." + Environment.NewLine
			+ ConsoleChatLoop.Prompt);
	}

	/// <summary>
	/// The first turn names no conversation — the runner names one — and every later turn names that one,
	/// so the session carries the earlier lines. A loop that started a conversation per line would answer
	/// every question as if it were the first.
	/// </summary>
	[Fact]
	public async Task The_Second_Line_Continues_The_Conversation_The_Runner_Named()
	{
		StreamingRunner runner = new("ok");
		ConsoleChatLoop loop = new(() => runner, new ConsoleStreams(new StringReader("first\nsecond\n"), new StringWriter(), false), Folder);

		await loop.RunAsync(CancellationToken.None);

		runner.Calls.Should().HaveCount(2);
		runner.Calls[0].Options?.ConversationId.Should().BeNull();
		runner.Calls[1].Options!.ConversationId.Should().Be(StreamingRunner.ConversationId);
	}

	[Fact]
	public async Task Exit_Ends_The_Loop_Without_Sending_Anything_Whatever_Its_Case_Or_Spacing()
	{
		StreamingRunner runner = new("never");
		ConsoleChatLoop loop = new(() => runner, new ConsoleStreams(new StringReader("  EXIT \nafter\n"), new StringWriter(), false), Folder);

		ConsoleLoopEnd end = await loop.RunAsync(CancellationToken.None);

		end.Should().Be(ConsoleLoopEnd.ExitRequested);
		runner.Calls.Should().BeEmpty();
	}

	[Fact]
	public async Task Blank_Lines_Are_Skipped_Not_Sent()
	{
		StreamingRunner runner = new("ok");
		ConsoleChatLoop loop = new(() => runner, new ConsoleStreams(new StringReader("\n   \nhello\n\n"), new StringWriter(), false), Folder);

		await loop.RunAsync(CancellationToken.None);

		runner.Calls.Should().ContainSingle().Which.Messages[0].Text.Should().Be("hello");
	}

	[Fact]
	public async Task The_End_Of_Input_Ends_The_Loop_As_Such_With_Nothing_Sent()
	{
		StreamingRunner runner = new("never");
		ConsoleChatLoop loop = new(() => runner, new ConsoleStreams(new StringReader(""), new StringWriter(), false), Folder);

		ConsoleLoopEnd end = await loop.RunAsync(CancellationToken.None);

		end.Should().Be(ConsoleLoopEnd.InputEnded);
		runner.Calls.Should().BeEmpty();
	}

	/// <summary>
	/// The console's reader cannot be interrupted, so a read is awaited against the token and abandoned
	/// when it fires. A loop that awaited the read itself would hold the host's shutdown until a key was
	/// pressed; this reader never returns until the test lets it.
	/// </summary>
	[Fact]
	public async Task Cancellation_While_Waiting_For_A_Line_Ends_The_Loop_Promptly()
	{
		using BlockingReader input = new();
		StreamingRunner runner = new("never");
		ConsoleChatLoop loop = new(() => runner, new ConsoleStreams(input, new StringWriter(), false), Folder);
		using CancellationTokenSource stopping = new(TimeSpan.FromMilliseconds(100));

		Stopwatch clock = Stopwatch.StartNew();
		ConsoleLoopEnd end = await loop.RunAsync(stopping.Token);

		end.Should().Be(ConsoleLoopEnd.Cancelled);
		clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the read is abandoned, not waited for");
		input.Release();
	}

	/// <summary>
	/// A turn that fails before its stream begins is said with the execution's own prefix and the loop
	/// reads the next line: a broken turn is the person's to see, never the console's to die on.
	/// </summary>
	[Fact]
	public async Task A_Turn_That_Throws_Is_Said_And_The_Loop_Goes_On()
	{
		StreamingRunner runner = new("second answer") { FaultFirstCallWith = new InvalidOperationException("no provider") };
		StringWriter output = new();
		ConsoleChatLoop loop = new(() => runner, new ConsoleStreams(new StringReader("one\ntwo\n"), output, false), Folder);

		ConsoleLoopEnd end = await loop.RunAsync(CancellationToken.None);

		end.Should().Be(ConsoleLoopEnd.InputEnded);
		runner.Calls.Should().HaveCount(2);
		output.ToString().Should().Contain(MicrosoftAgentExecution.FailurePrefix + "no provider" + Environment.NewLine);
		output.ToString().Should().Contain("second answer" + Environment.NewLine);
	}

	/// <summary>
	/// The runner is built on the first question, and a configuration that names no provider is refused
	/// there: the person sees the sentence naming the setting, the loop goes on, and the next question asks
	/// again rather than repeating a refusal that was the first attempt's.
	/// </summary>
	[Fact]
	public async Task A_Runner_That_Cannot_Be_Built_Is_Said_At_The_First_Question_And_Asked_For_Again()
	{
		StreamingRunner runner = new("built now");
		Int32 attempts = 0;
		Func<IChatClient> factory = () => ++attempts == 1 ? throw new InvalidOperationException("Provider:ApiKey is required") : runner;
		StringWriter output = new();
		ConsoleChatLoop loop = new(factory, new ConsoleStreams(new StringReader("one\ntwo\nthree\n"), output, false), Folder);

		await loop.RunAsync(CancellationToken.None);

		attempts.Should().Be(2, "a runner once built is kept; one that could not be is asked for again");
		runner.Calls.Should().HaveCount(2);
		output.ToString().Should().Contain(MicrosoftAgentExecution.FailurePrefix + "Provider:ApiKey is required" + Environment.NewLine);
	}

	[Fact]
	public async Task Cancellation_During_A_Turn_Ends_The_Loop_As_Cancelled()
	{
		StreamingRunner runner = new("never") { HangUntilCancelled = true };
		ConsoleChatLoop loop = new(() => runner, new ConsoleStreams(new StringReader("slow question\n"), new StringWriter(), false), Folder);
		using CancellationTokenSource stopping = new(TimeSpan.FromMilliseconds(100));

		ConsoleLoopEnd end = await loop.RunAsync(stopping.Token);

		end.Should().Be(ConsoleLoopEnd.Cancelled);
	}

	/// <summary>Streams its pieces as text updates, names the conversation, and records what it was asked.</summary>
	private sealed class StreamingRunner(params String[] pieces) : IChatClient
	{
		public const String ConversationId = "conversation-1";

		public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];

		public Exception? FaultFirstCallWith { get; init; }

		public Boolean HangUntilCancelled { get; init; }

		public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
			=> throw new NotSupportedException("The console streams every turn.");

		public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			this.Calls.Add(([.. messages], options));

			if (this.Calls.Count == 1 && this.FaultFirstCallWith is not null)
			{
				throw this.FaultFirstCallWith;
			}

			if (this.HangUntilCancelled)
			{
				await Task.Delay(Timeout.Infinite, cancellationToken);
			}

			foreach (String piece in pieces)
			{
				await Task.Yield();
				yield return new ChatResponseUpdate(ChatRole.Assistant, piece) { ConversationId = ConversationId };
			}
		}

		public Object? GetService(Type serviceType, Object? serviceKey = null) => null;

		public void Dispose()
		{
		}
	}

	/// <summary>A reader whose every read blocks until the test releases it — the console with nobody typing.</summary>
	private sealed class BlockingReader : TextReader
	{
		private readonly ManualResetEventSlim _released = new();

		public void Release() => this._released.Set();

		public override String? ReadLine()
		{
			this._released.Wait();
			return null;
		}

		protected override void Dispose(Boolean disposing)
		{
			if (disposing)
			{
				this._released.Dispose();
			}

			base.Dispose(disposing);
		}
	}
}
