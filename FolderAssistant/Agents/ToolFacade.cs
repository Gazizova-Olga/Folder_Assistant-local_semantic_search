using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace FolderAssistant.Agents;

/// <summary>Which failure contract a tool is under. The group is the contract, not a label.</summary>
internal enum ToolGroup
{
	/// <summary>A file tool: a failure is a string the model must report, and the turn goes on.</summary>
	File,

	/// <summary>A search tool: a failure ends the turn.</summary>
	Search,
}

/// <summary>How one tool call ended, as the log line says it.</summary>
internal enum ToolCallStatus
{
	Success,
	Failed,
	Cancelled,
}

/// <summary>
/// The facade every tool call goes through (SPEC-100): it times the call, writes one structured log line
/// for it, and applies the failure contract of the tool's group.
///
/// <para>
/// The two contracts differ on purpose. A <see cref="ToolGroup.File"/> tool that fails returns a string
/// beginning with <see cref="FailurePrefix"/>, so the model sees what went wrong and can say so — a
/// missing file or a refused path is the model's mistake to correct, and ending the turn over it would
/// end every turn that touched a wrong path. A <see cref="ToolGroup.Search"/> tool that fails throws
/// through, unchanged, and the turn ends: a swallowed retrieval fault is indistinguishable from "nothing
/// relevant", and a model that believed it had searched would answer from prior knowledge. The index's
/// own refusal while it builds is under the same contract, because a refusal observed is not a licence
/// to answer.
/// </para>
///
/// <para>
/// A cancellation of the caller's token is neither: it is classified by the token before the exception
/// type — an HTTP client reports its own deadline as a cancellation — and passes through both groups,
/// since a string nobody will read is not a report.
/// </para>
/// </summary>
internal sealed class ToolFacade : DelegatingAIFunction
{
	/// <summary>What a file tool's failure begins with. The model is told, in its instructions, what it means.</summary>
	internal const String FailurePrefix = "TOOL_FAILED: ";

	private readonly ILogger _logger;

	public ToolFacade(AIFunction inner, ToolGroup group, ILogger logger)
		: base(inner)
	{
		ArgumentNullException.ThrowIfNull(logger);
		this.Group = group;
		this._logger = logger;
	}

	public ToolGroup Group { get; }

	/// <summary>The string a file tool's failure becomes: the prefix, the tool, and what went wrong.</summary>
	internal static String FailureText(String tool, Exception exception)
		=> $"{FailurePrefix}{tool}: {exception.Message}";

	protected override async ValueTask<Object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
	{
		Int64 started = Stopwatch.GetTimestamp();
		try
		{
			Object? result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);
			this.Record(ToolCallStatus.Success, started, null);

			return result;
		}
		// The filter is where the classification happens, not the block: an exception the filter declines
		// leaves exactly as it was thrown, stack intact, which a rethrow from a block would not keep — and
		// the log line is written either way.
		catch (Exception exception) when (this.Handles(exception, started, cancellationToken))
		{
			return FailureText(this.Name, exception);
		}
	}

	private Boolean Handles(Exception exception, Int64 started, CancellationToken cancellationToken)
	{
		if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
		{
			this.Record(ToolCallStatus.Cancelled, started, null);

			return false;
		}

		this.Record(ToolCallStatus.Failed, started, exception);

		return this.Group == ToolGroup.File;
	}

	private void Record(ToolCallStatus status, Int64 started, Exception? exception)
	{
		Double latencyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
		if (exception is null)
		{
			this._logger.LogInformation(
				"tool name={Tool} group={Group} status={Status} latencyMs={LatencyMs:F1}",
				this.Name, this.Group, status, latencyMs);

			return;
		}

		// A file failure is a warning: the turn goes on, and the line is how an operator sees a model
		// fumbling paths. A search failure is an error, and the only one here, because it ended the turn.
		LogLevel level = this.Group == ToolGroup.File ? LogLevel.Warning : LogLevel.Error;
		this._logger.Log(
			level,
			exception,
			"tool name={Tool} group={Group} status={Status} error={ErrorType} latencyMs={LatencyMs:F1}",
			this.Name, this.Group, status, exception.GetType().Name, latencyMs);
	}
}
