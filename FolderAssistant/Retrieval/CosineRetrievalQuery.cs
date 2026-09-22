using FolderAssistant.Embedding;
using FolderAssistant.Indexing;
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
	private readonly IIndexState? _indexState;

	public CosineRetrievalQuery(IVectorizer vectorizer, IIndexState? indexState = null)
		: this(vectorizer, new SqliteBlobVectorStoreReader(), indexState)
	{
	}

	internal CosineRetrievalQuery(
		IVectorizer vectorizer,
		IVectorStoreReader vectorStoreReader,
		IIndexState? indexState = null)
	{
		ArgumentNullException.ThrowIfNull(vectorizer);
		ArgumentNullException.ThrowIfNull(vectorStoreReader);

		this._vectorizer = vectorizer;
		this._vectorStoreReader = vectorStoreReader;
		this._indexState = indexState;
	}

	public IReadOnlyList<RetrievalHit> Search(String databasePath, String queryText, RetrievalOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);

		if (options.TopK <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(options), "TopK must be greater than zero.");
		}

		RetrievalGuard.EnsureQueryable(this._indexState);

		ModelDescriptor descriptor = this._vectorizer.Descriptor;

		// Embedded as a query, not as a document: an asymmetric model treats the two sides differently,
		// and getting it wrong costs relevance without raising anything.
		IReadOnlyList<Single> queryVector = this._vectorizer.Vectorize(queryText, EmbeddingKind.Query).Vector;

		// Scoped to the active model version. Vectors from a different model occupy a different space,
		// so a similarity computed across them is a number with no meaning. A model version that was
		// never indexed returns nothing rather than falling back to another model's vectors.
		IReadOnlyList<StoredVector> candidates =
			this._vectorStoreReader.ReadVectorsByModelVersion(databasePath, descriptor.ModelVersionId);

		// The query is scored against every candidate, so what depends on the query alone is computed once
		// rather than per candidate: its components as a contiguous array, and its norm. Recomputed inside
		// the loop, the query's norm was a third of the arithmetic of every comparison, for the same number
		// each time.
		Single[] query = queryVector as Single[] ?? [.. queryVector];
		Double queryNorm = Math.Sqrt(SumOfSquares(query));

		List<(String ChunkId, Double Score)> scored = new(candidates.Count);

		foreach (StoredVector candidate in candidates)
		{
			if (candidate.Vector.Count != query.Length)
			{
				// Same model version, different dimension: the stored vectors were written by a build
				// that disagreed with this one. Scoring them would return plausible nonsense, so this
				// says what happened instead.
				throw new InvalidOperationException(
					$"Stored vector for chunk '{candidate.ChunkId}' has dimension {candidate.Vector.Count}, "
					+ $"but model '{descriptor.ModelVersionId}' produces {query.Length}. "
					+ "The index must be rebuilt.");
			}

			Double score = CosineSimilarity(query, queryNorm, candidate.Vector);

			if (score >= options.MinScore)
			{
				scored.Add((candidate.ChunkId, score));
			}
		}

		// Ties broken by chunk id so the order is total and repeatable. Without it, two chunks scoring
		// identically could swap places between runs, and a comparison against another backend would
		// report a difference that is not one.
		(String ChunkId, Double Score)[] ranked = [.. scored
			.OrderByDescending(static hit => hit.Score)
			.ThenBy(static hit => hit.ChunkId, StringComparer.Ordinal)
			.Take(options.TopK)];

		if (ranked.Length == 0)
		{
			return [];
		}

		// Locations are resolved only for what actually ranked. Joining the manifests for every
		// candidate instead spends the whole read fetching file paths and token offsets for rows that
		// are about to be discarded — see SPEC-131.
		IReadOnlyDictionary<String, ChunkLocation> locations =
			this._vectorStoreReader.ReadChunkLocations(databasePath, [.. ranked.Select(static hit => hit.ChunkId)]);

		List<RetrievalHit> hits = new(ranked.Length);

		foreach ((String chunkId, Double score) in ranked)
		{
			if (!locations.TryGetValue(chunkId, out ChunkLocation? location))
			{
				// The chunk was deleted between scoring and resolving. Dropping it is right: a hit
				// with no source is not a result, and the alternative is inventing a path.
				continue;
			}

			hits.Add(new RetrievalHit(
				chunkId,
				location.FilePath,
				location.ChunkIndex,
				location.TokenStart,
				location.TokenEnd,
				score,
				location.ChunkHash));
		}

		return hits;
	}

	/// <summary>
	/// The query's similarity to one stored vector, given the query's norm, which does not change across
	/// the scan.
	///
	/// <para>
	/// <strong>Every score is bit-identical to the single-loop form it replaced.</strong> Each sum
	/// accumulates the same <see cref="Single"/> products, widened to <see cref="Double"/>, in the same
	/// order, and the final division is the same expression. That is what lets this be a change to how
	/// much work a query does without any possibility of moving a ranking — and why the test pins exact
	/// equality rather than closeness.
	/// </para>
	///
	/// <para>
	/// Both readers hand back stored vectors as <see cref="Single"/> arrays, so the span loop is the one
	/// that runs: no interface call and no bounds check per component, in the loop every brute-force query
	/// runs dimension-times-corpus-size times. The indexed loop keeps a reader that returns some other list
	/// correct.
	/// </para>
	/// </summary>
	private static Double CosineSimilarity(Single[] query, Double queryNorm, IReadOnlyList<Single> candidate)
	{
		if (queryNorm <= 0)
		{
			return 0.0;
		}

		Double dot = 0.0;
		Double candidateNorm = 0.0;

		if (candidate is Single[] array)
		{
			ReadOnlySpan<Single> q = query;
			ReadOnlySpan<Single> c = array;

			for (Int32 i = 0; i < q.Length; i++)
			{
				dot += q[i] * c[i];
				candidateNorm += c[i] * c[i];
			}
		}
		else
		{
			for (Int32 i = 0; i < query.Length; i++)
			{
				dot += query[i] * candidate[i];
				candidateNorm += candidate[i] * candidate[i];
			}
		}

		if (candidateNorm <= 0)
		{
			return 0.0;
		}

		return dot / (queryNorm * Math.Sqrt(candidateNorm));
	}

	private static Double SumOfSquares(ReadOnlySpan<Single> vector)
	{
		Double sum = 0.0;

		for (Int32 i = 0; i < vector.Length; i++)
		{
			sum += vector[i] * vector[i];
		}

		return sum;
	}
}
