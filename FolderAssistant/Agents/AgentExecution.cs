using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FolderAssistant.Retrieval;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderAssistant.Agents;

/// <summary>Runs one turn of one conversation (SPEC-100).</summary>
internal interface IAgentExecution
{
	/// <summary>The whole turn. A failure is the exception itself.</summary>
	Task<AgentResponse> RunAsync(String conversationId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken);

	/// <summary>
	/// The turn as it is produced. A failure is the last update, as text, and the stream ends normally;
	/// only a cancellation of the caller's token throws.
	/// </summary>
	IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(String conversationId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken);
}

/// <summary>
/// A turn through the agent framework: the coordinator's session for the conversation is loaded, the
/// agent runs over it, and the session is saved when the turn succeeded — a failed turn leaves the stored
/// session as the last good turn left it.
///
/// <para>
/// The turn's telemetry is recorded here and not in a decorator over this class. A streamed turn that
/// fails yields its failure as an ordinary text update and drains normally, because by then the caller
/// has a response under way and text is the one channel left to say so; a wrapper would see a stream that
/// ended cleanly and record a success.
/// </para>
///
/// <para>
/// A fault is classified by the caller's token before the exception's type: an HTTP client reports its
/// own deadline as a cancellation, so a cancellation the caller did not ask for is a timeout. A stream
/// the caller stops reading is cancelled, not successful. The latency stops at the last update, before
/// the session is saved: the save is this application's bookkeeping and not the turn the caller waited for.
/// </para>
/// </summary>
internal sealed class MicrosoftAgentExecution : IAgentExecution
{
	/// <summary>What the text of a failed streamed turn begins with.</summary>
	internal const String FailurePrefix = "The turn failed: ";

	/// <summary>What follows a streamed answer whose session could not be saved.</summary>
	internal const String SaveFailurePrefix = "This turn could not be saved, so the conversation will not remember it: ";

	private readonly StaticWorkflowRoute _route;
	private readonly IAgentSessionStore _sessions;
	private readonly ITurnTelemetry _telemetry;
	private readonly ProviderErrorDescriber _errors;
	private readonly ILogger _logger;

	public MicrosoftAgentExecution(StaticWorkflowRoute route, IAgentSessionStore sessions, ITurnTelemetry telemetry, ProviderErrorDescriber errors, ILogger<MicrosoftAgentExecution>? logger = null)
	{
		ArgumentNullException.ThrowIfNull(route);
		ArgumentNullException.ThrowIfNull(sessions);
		ArgumentNullException.ThrowIfNull(telemetry);
		ArgumentNullException.ThrowIfNull(errors);
		this._route = route;
		this._sessions = sessions;
		this._telemetry = telemetry;
		this._errors = errors;
		this._logger = logger ?? (ILogger)NullLogger.Instance;
	}

	public async Task<AgentResponse> RunAsync(String conversationId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
		ArgumentNullException.ThrowIfNull(messages);

		Int64 started = Stopwatch.GetTimestamp();
		Double? answeredMs = null;
		try
		{
			AgentHandle handle = this._route.Coordinator;
			AgentSession session = await this.LoadSessionAsync(handle, conversationId, cancellationToken).ConfigureAwait(false);
			AgentResponse response = await handle.Agent.RunAsync(messages, session, cancellationToken: cancellationToken).ConfigureAwait(false);
			answeredMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

			await this.SaveSessionAsync(handle, conversationId, session, cancellationToken).ConfigureAwait(false);
			this.Record(streamed: false, answeredMs.Value, TurnStatus.Success, null);

			return response;
		}
		catch (Exception exception)
		{
			this.Record(
				streamed: false,
				answeredMs ?? Stopwatch.GetElapsedTime(started).TotalMilliseconds,
				Classify(exception, cancellationToken),
				exception);

			throw;
		}
	}

	public IAsyncEnumerable<AgentResponseUpdate> RunStreamingAsync(String conversationId, IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
		ArgumentNullException.ThrowIfNull(messages);

		return this.StreamAsync(conversationId, messages, cancellationToken);
	}

	/// <summary>How a turn that ended on <paramref name="exception"/> is filed: the caller's token first, the type second.</summary>
	internal static TurnStatus Classify(Exception exception, CancellationToken cancellationToken)
	{
		if (IsCallersCancellation(exception, cancellationToken))
		{
			return TurnStatus.Cancelled;
		}

		return exception switch
		{
			OperationCanceledException or TimeoutException => TurnStatus.TimedOut,
			IndexNotReadyException { IsBuildFailure: false } => TurnStatus.NotReady,
			_ => TurnStatus.Failed,
		};
	}

	private static Boolean IsCallersCancellation(Exception exception, CancellationToken cancellationToken)
		=> exception is OperationCanceledException && cancellationToken.IsCancellationRequested;

	private async IAsyncEnumerable<AgentResponseUpdate> StreamAsync(String conversationId, IReadOnlyList<ChatMessage> messages, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		Int64 started = Stopwatch.GetTimestamp();
		Boolean recorded = false;
		IAsyncEnumerator<AgentResponseUpdate>? updates = null;
		try
		{
			AgentHandle? handle = null;
			AgentSession? session = null;
			Exception? failure = null;
			try
			{
				handle = this._route.Coordinator;
				session = await this.LoadSessionAsync(handle, conversationId, cancellationToken).ConfigureAwait(false);
				updates = handle.Agent.RunStreamingAsync(messages, session, cancellationToken: cancellationToken).GetAsyncEnumerator(cancellationToken);
			}
			catch (Exception exception) when (!IsCallersCancellation(exception, cancellationToken))
			{
				failure = exception;
			}

			while (failure is null)
			{
				try
				{
					if (!await updates!.MoveNextAsync().ConfigureAwait(false))
					{
						break;
					}
				}
				catch (Exception exception) when (!IsCallersCancellation(exception, cancellationToken))
				{
					failure = exception;
					break;
				}

				yield return updates.Current;
			}

			Double answeredMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
			// The provider's failure as a sentence a person can act on; any other failure — a search refusal,
			// a tool's — already carries its own.
			String? note = failure is null ? null : FailurePrefix + this._errors.DescribeOrMessage(failure);
			if (failure is null)
			{
				try
				{
					await this.SaveSessionAsync(handle!, conversationId, session!, cancellationToken).ConfigureAwait(false);
				}
				catch (Exception exception) when (!IsCallersCancellation(exception, cancellationToken))
				{
					failure = exception;
					note = SaveFailurePrefix + exception.Message;
				}
			}

			// Recorded before the note is yielded, so a caller that stops reading at the note does not
			// turn a failure already known into a cancellation.
			recorded = true;
			this.Record(streamed: true, answeredMs, failure is null ? TurnStatus.Success : Classify(failure, cancellationToken), failure);

			if (note is not null)
			{
				yield return new AgentResponseUpdate(ChatRole.Assistant, note);
			}
		}
		finally
		{
			// Reached unrecorded in two ways, and both are the caller going away: its token was cancelled
			// and the exception is on its way out, or it stopped reading and disposed the stream.
			if (!recorded)
			{
				this.Record(streamed: true, Stopwatch.GetElapsedTime(started).TotalMilliseconds, TurnStatus.Cancelled, null);
			}

			if (updates is not null)
			{
				await updates.DisposeAsync().ConfigureAwait(false);
			}
		}
	}

	private async Task<AgentSession> LoadSessionAsync(AgentHandle handle, String conversationId, CancellationToken cancellationToken)
	{
		JsonElement? saved = await this._sessions.LoadAsync(handle.Name, conversationId, cancellationToken).ConfigureAwait(false);
		if (saved is null)
		{
			return await handle.Agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
		}

		try
		{
			return await handle.Agent.DeserializeSessionAsync(saved.Value, cancellationToken: cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (!IsCallersCancellation(exception, cancellationToken))
		{
			// A session that cannot be read starts the conversation's memory again rather than ending every
			// later turn of it: the blob is the framework's format, and a framework upgrade may not read
			// what the last one wrote.
			this._logger.LogWarning(
				exception,
				"The saved session of agent {Agent} for conversation {ConversationId} could not be read; the turn starts a fresh one.",
				handle.Name, conversationId);

			return await handle.Agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	private async Task SaveSessionAsync(AgentHandle handle, String conversationId, AgentSession session, CancellationToken cancellationToken)
	{
		JsonElement serialized = await handle.Agent.SerializeSessionAsync(session, cancellationToken: cancellationToken).ConfigureAwait(false);
		await this._sessions.SaveAsync(handle.Name, conversationId, serialized, cancellationToken).ConfigureAwait(false);
	}

	private void Record(Boolean streamed, Double latencyMs, TurnStatus status, Exception? exception)
		=> this._telemetry.Record(new TurnTelemetry(this._route.CoordinatorName, streamed, latencyMs, status, exception?.GetType().Name));
}
