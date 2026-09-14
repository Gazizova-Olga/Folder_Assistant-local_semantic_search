using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace FolderAssistant.Retrieval;

/// <summary>
/// Wraps an <see cref="IRetrievalQuery"/> to measure and emit per-call telemetry: it times every
/// <see cref="Search"/> and records one <see cref="RetrievalCallTelemetry"/>, on the refusal and
/// failure paths as well as the successful one.
///
/// <para>
/// The same argument that put embedding timing in one decorator applies here with more force. Two
/// backends implement this interface and the reason both exist is to be compared; timing measured
/// inside each of them would be two measurements of two things, and the comparison would be of the
/// instruments as much as of the backends.
/// </para>
///
/// <para>
/// No capability forwarding, unlike the embedding wrapper: <see cref="IRetrievalQuery"/> is one
/// method and carries no optional interfaces for a wrapper to hide.
/// </para>
/// </summary>
internal sealed class RetrievalTelemetryQuery : IRetrievalQuery
{
	private readonly IRetrievalQuery _inner;
	private readonly IRetrievalTelemetry _telemetry;
	private readonly String _backend;

	private RetrievalTelemetryQuery(IRetrievalQuery inner, IRetrievalTelemetry telemetry)
	{
		ArgumentNullException.ThrowIfNull(inner);
		ArgumentNullException.ThrowIfNull(telemetry);

		this._inner = inner;
		this._telemetry = telemetry;

		// Read once, from the instance actually composed. Which backend produced a number is the
		// whole point of recording it, and a profile name would only report what was asked for.
		this._backend = inner.GetType().Name;
	}

	public static IRetrievalQuery Wrap(IRetrievalQuery inner, IRetrievalTelemetry telemetry)
		=> new RetrievalTelemetryQuery(inner, telemetry);

	public IReadOnlyList<RetrievalHit> Search(String databasePath, String queryText, RetrievalOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);

		Stopwatch stopwatch = Stopwatch.StartNew();

		try
		{
			IReadOnlyList<RetrievalHit> hits = this._inner.Search(databasePath, queryText, options);

			stopwatch.Stop();
			this.Record(options, hits.Count, stopwatch, RetrievalStatus.Success);

			return hits;
		}
		catch (IndexNotReadyException ex)
		{
			stopwatch.Stop();

			// The refusal carries two conditions under one type. Only the still-building one is the
			// expected state this status exists to keep out of the error rate; a build that failed is
			// a fault, and recording it as "not ready yet" would describe a dead backend as a startup
			// condition that will clear on its own.
			Boolean failed = ex.IsBuildFailure;

			this.Record(
				options,
				0,
				stopwatch,
				failed ? RetrievalStatus.Failed : RetrievalStatus.NotReady,
				failed ? ex.InnerException?.GetType().Name ?? nameof(IndexNotReadyException) : null);

			// Rethrown either way. Refusing is the caller's signal to wait or degrade, and observing
			// it is not a licence to answer it.
			throw;
		}
		catch (Exception ex)
		{
			stopwatch.Stop();

			// A search embeds its query text before it ranks anything, so an embedding backend that
			// stops answering surfaces here rather than in the indexing path — as a timeout from the
			// embedder's own deadline (SPEC-162), or as a cancellation where a transport's deadline is
			// what ends the wait. Either is a fault outside the backend being measured, and folding it
			// into Failed would attribute it to whichever backend happened to be composed.
			RetrievalStatus status = ex is OperationCanceledException or TimeoutException
				? RetrievalStatus.TimedOut
				: RetrievalStatus.Failed;

			this.Record(options, 0, stopwatch, status, ex.GetType().Name);

			throw;
		}
	}

	private void Record(
		RetrievalOptions options,
		Int32 resultCount,
		Stopwatch stopwatch,
		RetrievalStatus status,
		String? errorCode = null)
		=> this._telemetry.Record(new RetrievalCallTelemetry(
			this._backend,
			options.TopK,
			resultCount,
			stopwatch.Elapsed.TotalMilliseconds,
			status,
			errorCode));
}

/// <summary>
/// The default sink. Writes each call through <see cref="ILogger"/> as a structured event, the way
/// the embedding sink does, and also records it through a <see cref="Meter"/> so the aggregates are
/// scrapeable directly rather than only computable from a log stream.
///
/// <para>
/// Both channels, not one. The log line is what someone reads when a single query behaved oddly;
/// the meter is what a dashboard reads, and it keeps working when the log level is raised to hide
/// routine successes — which is the first thing an operator does to a per-call log.
/// </para>
/// </summary>
internal sealed class LoggerRetrievalTelemetry : IRetrievalTelemetry
{
	internal const String MeterName = "FolderAssistant.Retrieval";

	private static readonly Meter Meter = new(MeterName);

	private static readonly Histogram<Double> SearchDuration = Meter.CreateHistogram<Double>(
		"retrieval.search.duration",
		unit: "ms",
		description: "Wall-clock of one IRetrievalQuery.Search call, tagged by backend and status.");

	private static readonly Counter<Int64> SearchCount = Meter.CreateCounter<Int64>(
		"retrieval.search.count",
		description: "IRetrievalQuery.Search calls, tagged by backend and status.");

	private readonly ILogger<LoggerRetrievalTelemetry> _logger;

	public LoggerRetrievalTelemetry(ILogger<LoggerRetrievalTelemetry> logger)
	{
		ArgumentNullException.ThrowIfNull(logger);

		this._logger = logger;
	}

	public void Record(RetrievalCallTelemetry call)
	{
		ArgumentNullException.ThrowIfNull(call);

		// Both tags on both instruments. The two backends must never be merged into one series —
		// they are a baseline and a candidate, and a merged latency figure describes neither.
		TagList tags = default;
		tags.Add("backend", call.Backend);
		tags.Add("status", call.Status.ToString());

		SearchDuration.Record(call.LatencyMs, tags);
		SearchCount.Add(1, tags);

		// NotReady logs at information, not warning, although it shares the failure lines' fields.
		// It is what every process reports until its first index finishes, and an alert keyed on
		// warning volume would fire on ordinary startup traffic.
		if (call.Status is RetrievalStatus.Success or RetrievalStatus.NotReady)
		{
			this._logger.LogInformation(
				"retrieval backend={Backend} topK={RequestedTopK} results={ResultCount} status={Status} "
				+ "latencyMs={LatencyMs:F1}",
				call.Backend, call.RequestedTopK, call.ResultCount, call.Status, call.LatencyMs);

			return;
		}

		this._logger.LogWarning(
			"retrieval backend={Backend} topK={RequestedTopK} results={ResultCount} status={Status} "
			+ "errorCode={ErrorCode} latencyMs={LatencyMs:F1}",
			call.Backend, call.RequestedTopK, call.ResultCount, call.Status, call.ErrorCode, call.LatencyMs);
	}
}
