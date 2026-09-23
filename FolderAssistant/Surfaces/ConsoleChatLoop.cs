using FolderAssistant.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderAssistant.Surfaces;

/// <summary>How the console loop ended, which is what decides whether the host stops with it.</summary>
internal enum ConsoleLoopEnd
{
	/// <summary>The person typed <c>exit</c>: they are done, and the host stops with the loop.</summary>
	ExitRequested,

	/// <summary>Standard input ended. Nobody will type again, but the host goes on serving.</summary>
	InputEnded,

	/// <summary>The caller's token was cancelled — the host is stopping.</summary>
	Cancelled,
}

/// <summary>
/// The console front end (SPEC-100): one conversation with the roster, a line at a time, through the
/// runner. Each non-blank line is one turn's user message, streamed back as it is produced and ended
/// with a newline; the conversation is the one the runner named on the first turn, so every later line
/// is answered with the earlier ones in the session. It reads and writes through
/// <see cref="ConsoleStreams"/> rather than the console itself, so the loop is checked over a scripted
/// reader and a writer that can be read back.
///
/// <para>
/// A turn's failure reaches the loop as text — the execution ends a failed streamed turn with its
/// failure as the last update — and is printed as such. What the loop catches itself is a failure
/// before the stream begins or outside it, printed with the same prefix, so a broken turn never ends
/// the console: the next line is read. Only the caller's own cancellation ends it that way.
/// </para>
///
/// <para>
/// The runner is asked for on the first question, not at construction. Building it builds the roster's
/// clients, and that is where a configuration naming no provider is refused (SPEC-140); the refusal
/// belongs to the first question, said at the prompt with the setting to fix, and never to the host's
/// start — a keyless host boots and serves its index regardless. Asked again on the next question if it
/// could not be built, because nothing about the console makes a refusal permanent.
/// </para>
///
/// <para>
/// A read is started on its own task and awaited against the caller's token, because the console's
/// reader is synchronous whatever the async overload says — <c>Console.In</c> wraps <c>ReadLine</c> in a
/// completed task — and a read nothing can interrupt would otherwise hold the host's shutdown for as
/// long as nobody pressed a key. A read the token cancelled is abandoned: the process that cancelled it
/// is stopping.
/// </para>
/// </summary>
internal sealed class ConsoleChatLoop
{
	/// <summary>The one word that ends the loop and the host, matched without regard to case or surrounding space.</summary>
	internal const String ExitCommand = "exit";

	internal const String Prompt = "> ";

	private readonly Func<IChatClient> _runnerFactory;
	private readonly ConsoleStreams _streams;
	private readonly String _folderPath;
	private readonly ILogger _logger;
	private IChatClient? _runner;
	private String? _conversationId;

	public ConsoleChatLoop(Func<IChatClient> runner, ConsoleStreams streams, String folderPath, ILogger? logger = null)
	{
		ArgumentNullException.ThrowIfNull(runner);
		ArgumentNullException.ThrowIfNull(streams);
		ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
		this._runnerFactory = runner;
		this._streams = streams;
		this._folderPath = folderPath;
		this._logger = logger ?? NullLogger.Instance;
	}

	/// <summary>The line written once before the first prompt.</summary>
	internal static String Banner(String folderPath)
		=> $"Folder Assistant — ask about {folderPath}. Type '{ExitCommand}' to stop.";

	/// <summary>Reads and answers until <c>exit</c>, the end of input, or the caller's cancellation.</summary>
	public async Task<ConsoleLoopEnd> RunAsync(CancellationToken cancellationToken)
	{
		TextWriter output = this._streams.Output;

		try
		{
			await output.WriteLineAsync(Banner(this._folderPath)).ConfigureAwait(false);

			while (true)
			{
				await output.WriteAsync(Prompt).ConfigureAwait(false);
				String? line = await this.ReadLineAsync(cancellationToken).ConfigureAwait(false);

				if (line is null)
				{
					return ConsoleLoopEnd.InputEnded;
				}

				line = line.Trim();

				if (line.Length == 0)
				{
					continue;
				}

				if (String.Equals(line, ExitCommand, StringComparison.OrdinalIgnoreCase))
				{
					return ConsoleLoopEnd.ExitRequested;
				}

				await this.AnswerAsync(line, cancellationToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			return ConsoleLoopEnd.Cancelled;
		}
	}

	private async Task<String?> ReadLineAsync(CancellationToken cancellationToken)
	{
		Task<String?> read = Task.Run(() => this._streams.Input.ReadLine(), CancellationToken.None);

		return await read.WaitAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task AnswerAsync(String line, CancellationToken cancellationToken)
	{
		TextWriter output = this._streams.Output;
		ChatOptions? options = this._conversationId is null ? null : new ChatOptions { ConversationId = this._conversationId };
		ChatMessage[] messages = [new ChatMessage(ChatRole.User, line)];

		try
		{
			this._runner ??= this._runnerFactory();

			await foreach (ChatResponseUpdate update in this._runner.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
			{
				this._conversationId ??= update.ConversationId;

				String text = update.Text;
				if (text.Length > 0)
				{
					await output.WriteAsync(text).ConfigureAwait(false);
				}
			}
		}
		catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
		{
			// Classified by the caller's token before the exception type, like every other fault here: an
			// unasked cancellation is a failure of the turn, and the person is told rather than left with a
			// prompt that answered nothing.
			this._logger.LogError(ex, "The console turn failed outside the stream: {Message}", ex.Message);
			await output.WriteAsync(MicrosoftAgentExecution.FailurePrefix + ex.Message).ConfigureAwait(false);
		}

		await output.WriteLineAsync().ConfigureAwait(false);
	}
}
