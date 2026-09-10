using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FolderAssistant.Embedding;

/// <summary>
/// Wraps an <see cref="IVectorizer"/> to measure and emit per-call telemetry: it times every
/// <see cref="VectorizeAsync"/>, stamps the measured latency onto each returned
/// <see cref="EmbeddingResult"/>, and records one <see cref="EmbeddingCallTelemetry"/> — on the failure
/// path as well as the success one, since a backend that is failing is exactly what an operator needs to
/// see.
///
/// <para>
/// Timing lives here rather than inside each vectorizer for the obvious reason and one less obvious one:
/// measuring in one place is the only way the numbers from different implementations are comparable,
/// which is the whole point of being able to swap them.
/// </para>
///
/// <para>
/// <strong>Compose through <see cref="Wrap"/>, never by constructing this directly.</strong> The
/// vectorizers carry optional capabilities the rest of the system discovers by type test — the cold-fit
/// path asks <c>is IFittableVectorizer</c>, startup asks <c>is IEmbeddingHealthCheck</c>. A wrapper that
/// did not re-expose them would answer "no" to both while still returning perfectly good vectors, which
/// would silently disable fitting and the startup probe rather than fail.
/// </para>
/// </summary>
internal class EmbeddingTelemetryVectorizer : IVectorizer, IDisposable
{
	protected readonly IVectorizer Inner;

	private readonly IEmbeddingTelemetry _telemetry;

	protected EmbeddingTelemetryVectorizer(IVectorizer inner, IEmbeddingTelemetry telemetry)
	{
		ArgumentNullException.ThrowIfNull(inner);
		ArgumentNullException.ThrowIfNull(telemetry);

		this.Inner = inner;
		this._telemetry = telemetry;
	}

	public ModelDescriptor Descriptor => this.Inner.Descriptor;

	public async ValueTask<IReadOnlyList<EmbeddingResult>> VectorizeAsync(
		IReadOnlyList<String> texts,
		EmbeddingKind kind,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(texts);

		ModelDescriptor descriptor = this.Inner.Descriptor;
		Stopwatch stopwatch = Stopwatch.StartNew();

		try
		{
			IReadOnlyList<EmbeddingResult> results =
				await this.Inner.VectorizeAsync(texts, kind, cancellationToken).ConfigureAwait(false);

			stopwatch.Stop();
			Double latencyMs = stopwatch.Elapsed.TotalMilliseconds;

			this._telemetry.Record(new EmbeddingCallTelemetry(
				descriptor.ProviderType,
				descriptor.ModelVersionId,
				descriptor.Dimension,
				texts.Count,
				latencyMs,
				EmbeddingStatus.Success));

			// The call's wall-clock, stamped onto every result it produced. Identical across the batch by
			// design — one call embedded them all — so an inspected vector still carries what it cost.
			EmbeddingResult[] stamped = new EmbeddingResult[results.Count];

			for (Int32 i = 0; i < results.Count; i++)
			{
				stamped[i] = results[i] with { LatencyMs = latencyMs };
			}

			return stamped;
		}
		catch (Exception ex)
		{
			stopwatch.Stop();

			// A cancellation is a different outcome from a backend that answered badly, and an error rate
			// that mixes them tells an operator to investigate a shutdown.
			EmbeddingStatus status = ex is OperationCanceledException
				? EmbeddingStatus.TimedOut
				: EmbeddingStatus.Failed;

			this._telemetry.Record(new EmbeddingCallTelemetry(
				descriptor.ProviderType,
				descriptor.ModelVersionId,
				descriptor.Dimension,
				texts.Count,
				stopwatch.Elapsed.TotalMilliseconds,
				status,
				ex.GetType().Name));

			throw;
		}
	}

	public void Dispose()
	{
		this.Dispose(true);
		GC.SuppressFinalize(this);
	}

	protected virtual void Dispose(Boolean disposing)
	{
		// The wrapper is what the container holds as the IVectorizer, so disposing it has to release the
		// inner one: the Ollama vectorizer owns a client, and the wrap would otherwise leak it at shutdown.
		if (disposing)
		{
			(this.Inner as IDisposable)?.Dispose();
		}
	}

	/// <summary>
	/// Wraps <paramref name="inner"/>, re-exposing whichever optional capability it implements.
	///
	/// <para>
	/// Each concrete vectorizer implements at most one of them — LSA fits, Ollama health-checks, the
	/// placeholder neither — so one forwarder per capability covers the set. A vectorizer implementing
	/// both would need a third, and this is where that would be noticed.
	/// </para>
	/// </summary>
	public static IVectorizer Wrap(IVectorizer inner, IEmbeddingTelemetry telemetry) => inner switch
	{
		IFittableVectorizer => new FittableEmbeddingTelemetryVectorizer(inner, telemetry),
		IEmbeddingHealthCheck => new HealthCheckEmbeddingTelemetryVectorizer(inner, telemetry),
		_ => new EmbeddingTelemetryVectorizer(inner, telemetry),
	};
}

/// <summary>Telemetry wrapper that also forwards the inner's <see cref="IFittableVectorizer"/>.</summary>
internal sealed class FittableEmbeddingTelemetryVectorizer(IVectorizer inner, IEmbeddingTelemetry telemetry)
	: EmbeddingTelemetryVectorizer(inner, telemetry), IFittableVectorizer
{
	private IFittableVectorizer Fittable => (IFittableVectorizer)this.Inner;

	public String Fit(IReadOnlyList<String> corpus) => this.Fittable.Fit(corpus);

	public void LoadFit(String artifactJson) => this.Fittable.LoadFit(artifactJson);
}

/// <summary>Telemetry wrapper that also forwards the inner's <see cref="IEmbeddingHealthCheck"/>.</summary>
internal sealed class HealthCheckEmbeddingTelemetryVectorizer(IVectorizer inner, IEmbeddingTelemetry telemetry)
	: EmbeddingTelemetryVectorizer(inner, telemetry), IEmbeddingHealthCheck
{
	public ValueTask CheckAsync(CancellationToken cancellationToken = default)
		=> ((IEmbeddingHealthCheck)this.Inner).CheckAsync(cancellationToken);
}

/// <summary>
/// The default sink: writes each call's fields through <see cref="ILogger"/> as a structured event —
/// information for a successful embed, warning for a failed one, because only one of those is something
/// an operator can act on.
/// </summary>
internal sealed class LoggerEmbeddingTelemetry : IEmbeddingTelemetry
{
	private readonly ILogger<LoggerEmbeddingTelemetry> _logger;

	public LoggerEmbeddingTelemetry(ILogger<LoggerEmbeddingTelemetry> logger)
	{
		ArgumentNullException.ThrowIfNull(logger);

		this._logger = logger;
	}

	public void Record(EmbeddingCallTelemetry call)
	{
		ArgumentNullException.ThrowIfNull(call);

		if (call.Status == EmbeddingStatus.Success)
		{
			this._logger.LogInformation(
				"embedding provider={ProviderType} model={ModelVersionId} dim={Dimension} "
				+ "count={RequestCount} status={Status} latencyMs={LatencyMs:F1}",
				call.ProviderType, call.ModelVersionId, call.Dimension, call.RequestCount, call.Status,
				call.LatencyMs);

			return;
		}

		this._logger.LogWarning(
			"embedding provider={ProviderType} model={ModelVersionId} dim={Dimension} "
			+ "count={RequestCount} status={Status} errorCode={ErrorCode} latencyMs={LatencyMs:F1}",
			call.ProviderType, call.ModelVersionId, call.Dimension, call.RequestCount, call.Status,
			call.ErrorCode, call.LatencyMs);
	}
}
