namespace FolderAssistant.Indexing;

/// <summary>A vector, and which model produced it.</summary>
internal sealed record EmbeddingResult(
	String ModelVersionId,
	String ProviderType,
	Int32 Dimension,
	IReadOnlyList<Single> Vector);

/// <summary>
/// A deterministic embedder: a character-bucket histogram, L2-normalized.
///
/// <para>
/// It carries no semantics — anagrams produce the same vector — and is not a retrieval candidate. It
/// exists so the pipeline has an end-to-end floor that runs with no model, no network and no
/// non-determinism, which is what makes the storage and retrieval paths testable before there is
/// anything real to embed with.
/// </para>
/// </summary>
internal sealed class ProgrammableEmbeddingVectorizer
{
	public EmbeddingResult Vectorize(String text, String modelVersionId, Int32 dimension)
	{
		if (dimension <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(dimension), "Vector dimension must be positive.");
		}

		Single[] vector = new Single[dimension];

		if (String.IsNullOrWhiteSpace(text))
		{
			return new EmbeddingResult(modelVersionId, "programmable", dimension, vector);
		}

		foreach (Char character in text)
		{
			vector[character % dimension] += 1.0f;
		}

		Normalize(vector);

		return new EmbeddingResult(modelVersionId, "programmable", dimension, vector);
	}

	/// <summary>
	/// Scales to unit length, so cosine similarity between two vectors is their dot product and a long
	/// chunk does not out-score a short one on length alone.
	/// </summary>
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
