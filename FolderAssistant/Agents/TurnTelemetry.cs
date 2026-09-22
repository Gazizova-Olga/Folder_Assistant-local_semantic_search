using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FolderAssistant.Agents;

/// <summary>How one turn ended (SPEC-100).</summary>
internal enum TurnStatus
{
	Success,

	/// <summary>The caller's token was cancelled, or a streamed turn was abandoned before it finished.</summary>
	Cancelled,

	/// <summary>A deadline nobody on the caller's side asked for: a timeout, or a cancellation the caller's token did not request.</summary>
	TimedOut,

	/// <summary>The turn ended on the index's still-building refusal — the expected state before the first index, not a fault.</summary>
	NotReady,

	Failed,
}

/// <summary>One turn, as recorded: which agent it entered, how it was asked for, how long it took and how it ended.</summary>
/// <param name="ErrorCode">The exception's type name on a failure. A log field only, never a metric tag: its values are unbounded.</param>
internal sealed record TurnTelemetry(String Agent, Boolean Streamed, Double LatencyMs, TurnStatus Status, String? ErrorCode);

internal interface ITurnTelemetry
{
	void Record(TurnTelemetry turn);
}

/// <summary>
/// The default sink: a structured log line and a <see cref="Meter"/>, the same two channels and for the
/// same reason as the retrieval sink — the line is what someone reads about one odd turn, the meter is
/// what a dashboard reads and keeps working when the log level is raised.
/// </summary>
internal sealed class LoggerTurnTelemetry : ITurnTelemetry
{
	internal const String MeterName = "FolderAssistant.Turns";

	private static readonly Meter Meter = new(MeterName);

	private static readonly Histogram<Double> TurnDuration = Meter.CreateHistogram<Double>(
		"agent.turn.duration",
		unit: "ms",
		description: "Wall-clock of one turn up to its last update, excluding the session save, tagged by agent, streamed and status.");

	private static readonly Counter<Int64> TurnCount = Meter.CreateCounter<Int64>(
		"agent.turn.count",
		description: "Turns, tagged by agent, streamed and status.");

	private readonly ILogger<LoggerTurnTelemetry> _logger;

	public LoggerTurnTelemetry(ILogger<LoggerTurnTelemetry> logger)
	{
		ArgumentNullException.ThrowIfNull(logger);

		this._logger = logger;
	}

	public void Record(TurnTelemetry turn)
	{
		ArgumentNullException.ThrowIfNull(turn);

		TagList tags = default;
		tags.Add("agent", turn.Agent);
		tags.Add("streamed", turn.Streamed);
		tags.Add("status", turn.Status.ToString());

		TurnDuration.Record(turn.LatencyMs, tags);
		TurnCount.Add(1, tags);

		// NotReady and Cancelled log at information: the first is what every process reports until its
		// first index finishes, the second is a user closing a tab, and neither is something to alert on.
		if (turn.Status is TurnStatus.Success or TurnStatus.Cancelled or TurnStatus.NotReady)
		{
			this._logger.LogInformation(
				"turn agent={Agent} streamed={Streamed} status={Status} latencyMs={LatencyMs:F1}",
				turn.Agent, turn.Streamed, turn.Status, turn.LatencyMs);

			return;
		}

		this._logger.LogWarning(
			"turn agent={Agent} streamed={Streamed} status={Status} errorCode={ErrorCode} latencyMs={LatencyMs:F1}",
			turn.Agent, turn.Streamed, turn.Status, turn.ErrorCode, turn.LatencyMs);
	}
}
