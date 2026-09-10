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
	String ProviderType);

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
