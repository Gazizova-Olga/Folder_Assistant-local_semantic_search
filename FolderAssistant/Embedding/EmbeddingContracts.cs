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

	IReadOnlyList<EmbeddingResult> Vectorize(
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
	String Fit(IReadOnlyList<String> corpus);
}

internal static class VectorizerExtensions
{
	/// <summary>Embeds one text. The contract is a batch because most backends charge per call.</summary>
	public static EmbeddingResult Vectorize(this IVectorizer vectorizer, String text, EmbeddingKind kind)
		=> vectorizer.Vectorize([text], kind)[0];
}
