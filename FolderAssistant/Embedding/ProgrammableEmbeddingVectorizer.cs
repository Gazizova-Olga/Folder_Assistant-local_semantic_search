namespace FolderAssistant.Embedding;

/// <summary>
/// A deterministic embedder: a character-bucket histogram, L2-normalized.
///
/// <para>
/// It carries no semantics — any anagram produces the same vector, so <c>"dog"</c> and <c>"god"</c>
/// are indistinguishable to it — and is not a retrieval candidate. It exists so the pipeline has an
/// end-to-end floor that runs with no model, no network and no non-determinism, and so a real
/// implementation has something it must measurably beat.
/// </para>
/// </summary>
internal sealed class ProgrammableEmbeddingVectorizer : IVectorizer
{
	public const String ProviderTypeName = "programmable";

	public ProgrammableEmbeddingVectorizer(String modelVersionId, Int32 dimension)
	{
		if (dimension <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(dimension), "Vector dimension must be positive.");
		}

		this.Descriptor = new ModelDescriptor(
			ModelVersionId: modelVersionId,
			ProviderType: ProviderTypeName,
			ModelName: "programmable-embedding",
			Dimension: dimension);
	}

	public ModelDescriptor Descriptor { get; }

	public ValueTask<IReadOnlyList<EmbeddingResult>> VectorizeAsync(
		IReadOnlyList<String> texts,
		EmbeddingKind kind,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(texts);

		// In-process arithmetic: it completes synchronously, so the ValueTask carries the finished
		// result with no thread hop and no allocation of a Task.
		EmbeddingResult[] results = new EmbeddingResult[texts.Count];

		for (Int32 i = 0; i < texts.Count; i++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			results[i] = this.VectorizeSingle(texts[i]);
		}

		return new ValueTask<IReadOnlyList<EmbeddingResult>>(results);
	}

	private EmbeddingResult VectorizeSingle(String text)
	{
		Int32 dimension = this.Descriptor.Dimension;
		Single[] vector = new Single[dimension];

		if (String.IsNullOrWhiteSpace(text))
		{
			return this.ToResult(vector);
		}

		foreach (Char character in text)
		{
			vector[character % dimension] += 1.0f;
		}

		Normalize(vector);

		return this.ToResult(vector);
	}

	private EmbeddingResult ToResult(Single[] vector)
		=> new(vector, this.Descriptor.ModelVersionId, this.Descriptor.Dimension, this.Descriptor.ProviderType);

	private static void Normalize(Single[] vector)
	{
		Double sumSquares = 0.0;

		for (Int32 i = 0; i < vector.Length; i++)
		{
			sumSquares += vector[i] * vector[i];
		}

		if (sumSquares <= 0)
		{
			return;
		}

		Single norm = (Single)Math.Sqrt(sumSquares);

		for (Int32 i = 0; i < vector.Length; i++)
		{
			vector[i] /= norm;
		}
	}
}
