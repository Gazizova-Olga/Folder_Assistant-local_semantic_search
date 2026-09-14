namespace FolderAssistant.Embedding;

/// <summary>
/// Which side of a retrieval pair a text is. Several trained models are asymmetric and expect a
/// different instruction prefix for an indexed passage than for a search query — dropping the
/// distinction would handicap them silently, and make any comparison between implementations unfair.
/// A symmetric implementation may ignore it.
/// </summary>
internal enum EmbeddingKind
{
	Document,
	Query,
}

/// <summary>
/// What an embedding implementation says about itself.
///
/// <para>
/// The dimension is reported here rather than read from configuration: for a trained model it is
/// fixed by the weights, so it is not something a deployment gets to choose.
/// </para>
/// </summary>
internal sealed record ModelDescriptor(
	String ModelVersionId,
	String ProviderType,
	String ModelName,
	Int32 Dimension,
	String DistanceMetric = "cosine");

/// <summary>One embedded text, and the model that embedded it.</summary>
internal sealed record EmbeddingResult(
	IReadOnlyList<Single> Vector,
	String ModelVersionId,
	Int32 Dimension,
	String ProviderType,

	/// <summary>
	/// Wall-clock of the call that produced this vector. It is a property of the batch <em>call</em>
	/// rather than of the text, so it is identical across a batch's results — one round-trip embedded
	/// them all. Zero until a telemetry decorator stamps it: a vectorizer does not time itself.
	/// </summary>
	Double LatencyMs = 0);

/// <summary>
/// An embedding implementation. Implementations are interchangeable, and their vectors coexist in one
/// folder database keyed by <c>(chunk_id, model_version_id)</c>, so switching one does not invalidate
/// what another already stored.
/// </summary>
internal interface IVectorizer
{
	ModelDescriptor Descriptor { get; }

	/// <summary>
	/// Embeds a batch of texts.
	///
	/// <para>
	/// Asynchronous because a vectorizer may be network-bound — a local model server is still a socket
	/// (<c>SPEC-162</c>). In-process implementations complete synchronously and consume no thread, so the
	/// contract costs them nothing; a network-bound one can be awaited rather than blocking a thread per
	/// file while the indexing path waits on a round-trip.
	/// </para>
	/// </summary>
	ValueTask<IReadOnlyList<EmbeddingResult>> VectorizeAsync(
		IReadOnlyList<String> texts,
		EmbeddingKind kind,
		CancellationToken cancellationToken = default);
}

/// <summary>
/// An implementation whose output depends on corpus statistics, and which therefore has to be fitted
/// before it can embed anything. <c>Fit</c> returns a serialized artifact so the same fit that produced
/// the indexed vectors can be restored to embed a query against them.
/// </summary>
internal interface IFittableVectorizer : IVectorizer
{
	/// <summary>Fits against the corpus and returns the artifact to persist.</summary>
	String Fit(IReadOnlyList<String> corpus);

	/// <summary>
	/// Adopts a fit that was already persisted, instead of computing a new one.
	///
	/// <para>
	/// Incremental indexing reuses the existing fit so that newly embedded chunks land in the same
	/// space as the ones already stored. Refitting would change the projection and invalidate every
	/// vector under that model version, so it stays a deliberate operation under a new one — not
	/// something an ordinary edit to a file triggers.
	/// </para>
	/// </summary>
	void LoadFit(String artifactJson);
}

/// <summary>
/// An optional capability a vectorizer may implement to be probed <em>before</em> the first index runs.
///
/// <para>
/// Only a network-bound embedder needs it: an in-process one cannot be unreachable, so it does not
/// implement the seam and the probe is skipped for it entirely. That is why this is a separate
/// interface rather than a method on <see cref="IVectorizer"/> returning "healthy" — most
/// implementations have nothing to answer.
/// </para>
///
/// <para>
/// The indexing service checks for it and, when present, awaits <see cref="CheckAsync"/> at the top of
/// the first pass. A throw becomes a failed index carrying that message, which is one clear failure
/// instead of a wall of per-file delivery errors that each describe a symptom rather than the cause.
/// </para>
/// </summary>
internal interface IEmbeddingHealthCheck
{
	/// <summary>
	/// Verifies the backend is reachable and the model usable, throwing an
	/// <see cref="InvalidOperationException"/> with an actionable message when it is not. Returns
	/// normally when the backend answered a probe.
	/// </summary>
	ValueTask CheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The outcome of a single embedding call, as recorded in telemetry.
///
/// <para>
/// This appears only on a telemetry event, never on an <see cref="EmbeddingResult"/>. A materialised
/// result is the product of a successful call by construction — the contract throws rather than
/// returning a vector it could not produce — so a status field on the result could only ever read
/// <see cref="Success"/>.
/// </para>
/// </summary>
internal enum EmbeddingStatus
{
	Success,

	/// <summary>The backend answered badly, or not at all for a reason that was not time.</summary>
	Failed,

	/// <summary>
	/// The call hit a deadline — the vectorizer's own, or a transport's — and nobody had asked for it
	/// to stop. A fault in the backend, and the one that would otherwise hold a delivery in flight
	/// forever (<c>SPEC-162</c>).
	/// </summary>
	TimedOut,

	/// <summary>
	/// The caller cancelled. Not a fault: a process shutting down mid-embed is the ordinary case, and
	/// an error rate that counted it would tell an operator to investigate a normal exit. Classified by
	/// the caller's token before the exception type, because a transport reports its own deadline as a
	/// cancellation too.
	/// </summary>
	Cancelled,
}

/// <summary>
/// One embedding call's observability record.
///
/// <para>
/// This is the per-call primitive. The aggregates worth watching — success rate, latency
/// percentiles, cost — are computed downstream from a stream of these rather than emitted here,
/// because an aggregate computed at the source cannot be sliced afterwards by anything it did not
/// already group by.
/// </para>
/// </summary>
internal sealed record EmbeddingCallTelemetry(
	String ProviderType,
	String ModelVersionId,
	Int32 Dimension,
	Int32 RequestCount,
	Double LatencyMs,
	EmbeddingStatus Status,
	String? ErrorCode = null);

/// <summary>
/// Sink for per-call embedding telemetry. A seam rather than a logging call inside the embedding
/// path, so that emission is testable and a metrics backend can replace the default sink without
/// anything on that path changing.
/// </summary>
internal interface IEmbeddingTelemetry
{
	void Record(EmbeddingCallTelemetry call);
}

internal static class VectorizerExtensions
{
	/// <summary>
	/// Synchronous convenience over <see cref="IVectorizer.VectorizeAsync"/>, for the callers that stay
	/// synchronous — the retrieval query path embeds a single query vector per call, and making it async
	/// would ripple through the whole retrieval stack for no throughput gain, while the cold-fit pipeline
	/// is a batch job with nothing to overlap.
	///
	/// <para>
	/// In-process vectorizers complete synchronously, so this blocks no thread. A network-bound one blocks
	/// only the caller of that single embed, never the indexing path, which awaits <c>VectorizeAsync</c>.
	/// </para>
	/// </summary>
	public static IReadOnlyList<EmbeddingResult> Vectorize(
		this IVectorizer vectorizer,
		IReadOnlyList<String> texts,
		EmbeddingKind kind,
		CancellationToken cancellationToken = default)
		=> vectorizer.VectorizeAsync(texts, kind, cancellationToken).AsTask().GetAwaiter().GetResult();

	/// <summary>Embeds one text. The contract is a batch because most backends charge per call.</summary>
	public static EmbeddingResult Vectorize(this IVectorizer vectorizer, String text, EmbeddingKind kind)
		=> vectorizer.Vectorize([text], kind)[0];
}
