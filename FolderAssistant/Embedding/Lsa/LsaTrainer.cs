using MathNet.Numerics.LinearAlgebra;

namespace FolderAssistant.Embedding.Lsa;

/// <summary>
/// Fits an <see cref="LsaModel"/> from a corpus: a TF-IDF document/term matrix, reduced by a
/// truncated latent semantic decomposition.
/// </summary>
/// <remarks>
/// <para>
/// The reduction is computed from the document-space Gram matrix — <c>A·Aᵀ</c>, sized
/// documents × documents — and its symmetric eigendecomposition, rather than from a full SVD of
/// <c>A</c>. A full SVD materialises a vocabulary × vocabulary factor, which is quadratic in the
/// vocabulary and dominates the cost. The Gram route is cheap whenever a corpus has far fewer chunks
/// than distinct terms, which is the folder-scoped case.
/// </para>
/// </remarks>
internal sealed class LsaTrainer
{
	private readonly TextAnalyzer _analyzer;
	private readonly Boolean _includeTrigrams;
	private readonly Int32 _minDocumentFrequency;
	private readonly Int32 _maxVocabulary;

	public LsaTrainer(
		TextAnalyzer analyzer,
		Boolean includeTrigrams,
		Int32 minDocumentFrequency = 2,
		Int32 maxVocabulary = 4096)
	{
		ArgumentNullException.ThrowIfNull(analyzer);

		this._analyzer = analyzer;
		this._includeTrigrams = includeTrigrams;
		this._minDocumentFrequency = Math.Max(1, minDocumentFrequency);
		this._maxVocabulary = Math.Max(1, maxVocabulary);
	}

	public LsaModel Fit(IReadOnlyList<String> corpus, Int32 targetDimension)
	{
		ArgumentNullException.ThrowIfNull(corpus);

		if (corpus.Count == 0)
		{
			throw new ArgumentException("Cannot fit a model on an empty corpus.", nameof(corpus));
		}

		IReadOnlyList<String>[] analyzed = corpus.Select(this._analyzer.Analyze).ToArray();

		(IReadOnlyList<String> terms, IReadOnlyDictionary<String, Int32> termIndex) = this.BuildVocabulary(analyzed);

		if (terms.Count == 0)
		{
			throw new InvalidOperationException("The corpus produced no usable terms after analysis and pruning.");
		}

		IReadOnlyList<Single> idf = ComputeIdf(analyzed, terms, termIndex);
		Matrix<Double> a = BuildWeightedMatrix(analyzed, termIndex, idf);

		// The rank is bounded by both dimensions of the matrix. A k larger than the corpus can support
		// is not merely wasteful, it is meaningless.
		Int32 maxRank = Math.Min(corpus.Count, terms.Count);
		Int32 k = Math.Clamp(targetDimension, 1, maxRank);

		IReadOnlyList<IReadOnlyList<Single>> projection = ComputeProjection(a, k);

		return new LsaModel(
			terms,
			idf,
			projection,
			projection.Count == 0 ? 0 : projection[0].Count,
			this._includeTrigrams);
	}

	private (IReadOnlyList<String> Terms, IReadOnlyDictionary<String, Int32> Index) BuildVocabulary(
		IReadOnlyList<String>[] analyzed)
	{
		Dictionary<String, Int32> documentFrequency = new(StringComparer.Ordinal);

		foreach (IReadOnlyList<String> document in analyzed)
		{
			foreach (String term in document.Distinct(StringComparer.Ordinal))
			{
				documentFrequency[term] = documentFrequency.GetValueOrDefault(term) + 1;
			}
		}

		// Ordered by frequency to choose what survives the cap, then re-ordered by term so the stored
		// vocabulary is stable: the same corpus must fit to the same artifact.
		String[] terms = documentFrequency
			.Where(pair => pair.Value >= this._minDocumentFrequency)
			.OrderByDescending(static pair => pair.Value)
			.ThenBy(static pair => pair.Key, StringComparer.Ordinal)
			.Take(this._maxVocabulary)
			.Select(static pair => pair.Key)
			.OrderBy(static term => term, StringComparer.Ordinal)
			.ToArray();

		Dictionary<String, Int32> index = new(terms.Length, StringComparer.Ordinal);

		for (Int32 i = 0; i < terms.Length; i++)
		{
			index[terms[i]] = i;
		}

		return (terms, index);
	}

	private static IReadOnlyList<Single> ComputeIdf(
		IReadOnlyList<String>[] analyzed,
		IReadOnlyList<String> terms,
		IReadOnlyDictionary<String, Int32> termIndex)
	{
		Int32[] documentFrequency = new Int32[terms.Count];

		foreach (IReadOnlyList<String> document in analyzed)
		{
			foreach (String term in document.Distinct(StringComparer.Ordinal))
			{
				if (termIndex.TryGetValue(term, out Int32 column))
				{
					documentFrequency[column]++;
				}
			}
		}

		Int32 n = analyzed.Length;
		Single[] idf = new Single[terms.Count];

		for (Int32 i = 0; i < idf.Length; i++)
		{
			idf[i] = (Single)Math.Log((1.0 + n) / (1.0 + documentFrequency[i])) + 1.0f;
		}

		return idf;
	}

	private static Matrix<Double> BuildWeightedMatrix(
		IReadOnlyList<String>[] analyzed,
		IReadOnlyDictionary<String, Int32> termIndex,
		IReadOnlyList<Single> idf)
	{
		Matrix<Double> a = Matrix<Double>.Build.Dense(analyzed.Length, idf.Count);

		for (Int32 row = 0; row < analyzed.Length; row++)
		{
			Dictionary<Int32, Int32> counts = [];

			foreach (String term in analyzed[row])
			{
				if (termIndex.TryGetValue(term, out Int32 column))
				{
					counts[column] = counts.GetValueOrDefault(column) + 1;
				}
			}

			foreach ((Int32 column, Int32 count) in counts)
			{
				// Sublinear term frequency: a term repeated twenty times is not twenty times as
				// informative as one seen once.
				a[row, column] = (1.0 + Math.Log(count)) * idf[column];
			}
		}

		return a;
	}

	private static IReadOnlyList<IReadOnlyList<Single>> ComputeProjection(Matrix<Double> a, Int32 k)
	{
		// G = A·Aᵀ = U·S²·Uᵀ, so the eigenvectors of the Gram matrix are the left singular vectors.
		Matrix<Double> gram = a * a.Transpose();
		var evd = gram.Evd(Symmetricity.Symmetric);

		Int32 documentCount = a.RowCount;

		// Eigenvalues come back ascending, so the strongest concepts are the trailing columns.
		List<(Int32 Column, Double Value)> selected = [];

		for (Int32 i = documentCount - 1; i >= 0 && selected.Count < k; i--)
		{
			Double value = evd.EigenValues[i].Real;

			if (value > 1e-9)
			{
				selected.Add((i, value));
			}
		}

		Int32 effectiveK = selected.Count;
		Int32 vocabulary = a.ColumnCount;

		Single[][] projection = new Single[vocabulary][];

		for (Int32 term = 0; term < vocabulary; term++)
		{
			projection[term] = new Single[effectiveK];
		}

		// P = Aᵀ·W_k·diag(1/√λ_k) gives the right singular vectors, so embedding(x) = x·P places a
		// document at W·Σ and folds a query through the identical transform.
		//
		// The scaling must be 1/√λ, which is 1/Σ — NOT 1/λ. Dividing by λ cancels Σ out of the
		// transform entirely, weighting a weak noise concept exactly as heavily as the dominant one,
		// and the similarity structure the reduction exists to produce is destroyed.
		for (Int32 c = 0; c < effectiveK; c++)
		{
			(Int32 column, Double value) = selected[c];
			Double inverse = 1.0 / Math.Sqrt(value);

			for (Int32 term = 0; term < vocabulary; term++)
			{
				Double sum = 0.0;

				for (Int32 document = 0; document < documentCount; document++)
				{
					sum += a[document, term] * evd.EigenVectors[document, column];
				}

				projection[term][c] = (Single)(sum * inverse);
			}
		}

		return projection;
	}
}
