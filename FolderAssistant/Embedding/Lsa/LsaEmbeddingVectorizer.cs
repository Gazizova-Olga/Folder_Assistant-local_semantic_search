namespace FolderAssistant.Embedding.Lsa;

/// <summary>
/// A corpus-fitted embedder. Unlike the character-histogram baseline it captures weak synonymy:
/// terms occurring in similar contexts across the analyzed folder end up near each other after the
/// reduction, so a query for one can retrieve documents that only use the other.
///
/// <para>
/// It carries no pretrained weights and runs entirely offline, which also bounds it — on a
/// paraphrase query it stays below a trained transformer embedding by construction. What it offers
/// is semantics fitted to <em>this</em> folder, with nothing to download and nothing to reach.
/// </para>
/// </summary>
internal sealed class LsaEmbeddingVectorizer : IFittableVectorizer
{
	public const String ProviderTypeName = "programmable-lsa";

	private TextAnalyzer _analyzer;
	private readonly Boolean _includeTrigrams;
	private readonly Int32 _targetDimension;
	private readonly String _modelVersionId;

	private LsaModel? _model;

	private LsaEmbeddingVectorizer(
		String modelVersionId,
		Int32 targetDimension,
		Boolean includeTrigrams,
		LsaModel? model)
	{
		if (targetDimension <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(targetDimension), "Vector dimension must be positive.");
		}

		this._modelVersionId = modelVersionId;
		this._targetDimension = targetDimension;
		this._includeTrigrams = includeTrigrams;
		this._analyzer = new TextAnalyzer(includeTrigrams);
		this._model = model;
	}

	/// <summary>An unfitted instance, for indexing. <see cref="Fit"/> has to run before it can embed.</summary>
	public static LsaEmbeddingVectorizer CreateForFitting(
		String modelVersionId,
		Int32 targetDimension,
		Boolean includeTrigrams = true)
		=> new(modelVersionId, targetDimension, includeTrigrams, model: null);

	/// <summary>
	/// Rebuilds a fitted instance from a persisted artifact, for embedding a query.
	///
	/// <para>
	/// The analyzer configuration comes from the artifact and never from the caller. A query analyzed
	/// differently from the corpus is projected from a different feature space — which does not
	/// throw, it just retrieves the wrong things.
	/// </para>
	/// </summary>
	public static LsaEmbeddingVectorizer FromArtifact(String modelVersionId, String artifactJson)
	{
		LsaModel model = LsaModel.Deserialize(artifactJson);

		return new(modelVersionId, Math.Max(1, model.Dimension), model.IncludeTrigrams, model);
	}

	/// <summary>
	/// The dimension reported is the fitted model's <em>actual</em> rank, which can be below the
	/// requested target when the corpus cannot support it. Reporting the real number is what keeps
	/// the registry and the stored vectors agreeing with each other.
	/// </summary>
	public ModelDescriptor Descriptor => new(
		ModelVersionId: this._modelVersionId,
		ProviderType: ProviderTypeName,
		ModelName: "lsa-tfidf-svd",
		Dimension: this._model?.Dimension ?? this._targetDimension);

	public String Fit(IReadOnlyList<String> corpus)
	{
		this._model = new LsaTrainer(this._analyzer, this._includeTrigrams).Fit(corpus, this._targetDimension);

		return this._model.Serialize();
	}

	public void LoadFit(String artifactJson)
	{
		LsaModel model = LsaModel.Deserialize(artifactJson);

		// The analyzer is rebuilt from the artifact, never from this instance's construction
		// arguments. Features extracted differently from the corpus would be projected out of a
		// different space, and nothing would report it.
		this._analyzer = new TextAnalyzer(model.IncludeTrigrams);
		this._model = model;
	}

	public IReadOnlyList<EmbeddingResult> Vectorize(
		IReadOnlyList<String> texts,
		EmbeddingKind kind,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(texts);

		LsaModel model = this._model
			?? throw new InvalidOperationException(
				$"The model '{this._modelVersionId}' is not fitted. Call Fit, or rebuild it from a stored "
				+ "artifact; embedding against an unfitted projection would not match the indexed vectors.");

		EmbeddingResult[] results = new EmbeddingResult[texts.Count];

		for (Int32 i = 0; i < texts.Count; i++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			results[i] = this.Transform(model, texts[i]);
		}

		return results;
	}

	private EmbeddingResult Transform(LsaModel model, String text)
	{
		Single[] vector = new Single[model.Dimension];
		Dictionary<Int32, Int32> counts = [];

		foreach (String term in this._analyzer.Analyze(text))
		{
			if (model.TermIndex.TryGetValue(term, out Int32 column))
			{
				counts[column] = counts.GetValueOrDefault(column) + 1;
			}
		}

		// Folded into concept space with the same TF-IDF weighting the fit used. Text made entirely of
		// out-of-vocabulary terms yields the zero vector, which scores zero against everything — the
		// honest answer for a query the corpus has no words for.
		foreach ((Int32 column, Int32 count) in counts)
		{
			Single weight = (Single)((1.0 + Math.Log(count)) * model.Idf[column]);
			IReadOnlyList<Single> projectionRow = model.Projection[column];

			for (Int32 c = 0; c < vector.Length; c++)
			{
				vector[c] += weight * projectionRow[c];
			}
		}

		Normalize(vector);

		return new EmbeddingResult(vector, this._modelVersionId, model.Dimension, ProviderTypeName);
	}

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
