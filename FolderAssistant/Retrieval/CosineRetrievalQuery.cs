using FolderAssistant.Embedding;
using FolderAssistant.Persistence;

namespace FolderAssistant.Retrieval;

/// <summary>
/// Brute-force cosine similarity over every vector stored for the active model version.
///
/// <para>
/// This is the baseline a candidate vector backend has to beat before it is worth adopting, not
/// scaffolding to be replaced on sight. It has two properties a native index has to earn: it is
/// exact, and it has no availability story — no per-platform binary, nothing to detect.
/// </para>
/// </summary>
internal sealed class CosineRetrievalQuery : IRetrievalQuery
{
	private readonly IVectorizer _vectorizer;
	private readonly IVectorStoreReader _vectorStoreReader;

	public CosineRetrievalQuery(IVectorizer vectorizer)
		: this(vectorizer, new SqliteJsonVectorStoreReader())
	{
	}

	internal CosineRetrievalQuery(IVectorizer vectorizer, IVectorStoreReader vectorStoreReader)
	{
		ArgumentNullException.ThrowIfNull(vectorizer);
		ArgumentNullException.ThrowIfNull(vectorStoreReader);

		this._vectorizer = vectorizer;
		this._vectorStoreReader = vectorStoreReader;
	}

	public IReadOnlyList<RetrievalHit> Search(String databasePath, String queryText, RetrievalOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);

		if (options.TopK <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(options), "TopK must be greater than zero.");
		}

		ModelDescriptor descriptor = this._vectorizer.Descriptor;

		// Embedded as a query, not as a document: an asymmetric model treats the two sides differently,
		// and getting it wrong costs relevance without raising anything.
		IReadOnlyList<Single> queryVector = this._vectorizer.Vectorize(queryText, EmbeddingKind.Query).Vector;

		// Scoped to the active model version. Vectors from a different model occupy a different space,
		// so a similarity computed across them is a number with no meaning. A model version that was
		// never indexed returns nothing rather than falling back to another model's vectors.
		IReadOnlyList<StoredChunkVector> candidates =
			this._vectorStoreReader.ReadByModelVersion(databasePath, descriptor.ModelVersionId);

		List<RetrievalHit> hits = new(candidates.Count);

		foreach (StoredChunkVector candidate in candidates)
		{
			if (candidate.Vector.Count != queryVector.Count)
			{
				// Same model version, different dimension: the stored vectors were written by a build
				// that disagreed with this one. Scoring them would return plausible nonsense, so this
				// says what happened instead.
				throw new InvalidOperationException(
					$"Stored vector for chunk '{candidate.ChunkId}' has dimension {candidate.Vector.Count}, "
					+ $"but model '{descriptor.ModelVersionId}' produces {queryVector.Count}. "
					+ "The index must be rebuilt.");
			}

			Double score = CosineSimilarity(queryVector, candidate.Vector);

			if (score < options.MinScore)
			{
				continue;
			}

			hits.Add(new RetrievalHit(
				candidate.ChunkId,
				candidate.FilePath,
				candidate.ChunkIndex,
				candidate.TokenStart,
				candidate.TokenEnd,
				score));
		}

		// Ties broken by chunk id so the order is total and repeatable. Without it, two chunks scoring
		// identically could swap places between runs, and a comparison against another backend would
		// report a difference that is not one.
		return hits
			.OrderByDescending(static hit => hit.Score)
			.ThenBy(static hit => hit.ChunkId, StringComparer.Ordinal)
			.Take(options.TopK)
			.ToArray();
	}

	private static Double CosineSimilarity(IReadOnlyList<Single> left, IReadOnlyList<Single> right)
	{
		Double dot = 0.0;
		Double leftNorm = 0.0;
		Double rightNorm = 0.0;

		for (Int32 i = 0; i < left.Count; i++)
		{
			dot += left[i] * right[i];
			leftNorm += left[i] * left[i];
			rightNorm += right[i] * right[i];
		}

		if (leftNorm <= 0 || rightNorm <= 0)
		{
			return 0.0;
		}

		return dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
	}
}
