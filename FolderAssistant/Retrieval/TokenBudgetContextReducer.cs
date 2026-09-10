namespace FolderAssistant.Retrieval;

/// <summary>
/// The default <see cref="IContextReduction"/>: hybrid rerank plus Maximal Marginal Relevance selection
/// under a token budget.
///
/// <para>
/// Entirely in-process and dependency-free. Relevance blends the retrieval score with lexical query-term
/// coverage, and diversity is measured from the passage text itself — Jaccard over word sets — so no
/// vectors have to be threaded through the reduction contract and no model is called to decide what the
/// model should read. That matters for a stage on the query path: it costs nothing an operator has to
/// wait for.
/// </para>
/// </summary>
internal sealed class TokenBudgetContextReducer : IContextReduction
{
	public IReadOnlyList<ReducedPassage> Reduce(
		String query,
		IReadOnlyList<RetrievalCandidate> candidates,
		ContextBudget budget)
	{
		ArgumentNullException.ThrowIfNull(candidates);
		ArgumentNullException.ThrowIfNull(budget);

		if (candidates.Count == 0)
		{
			return [];
		}

		Int32 maxPassages = Math.Max(1, budget.MaxPassages);
		Int32 maxTokens = Math.Max(1, budget.MaxTokens);
		Double lambda = Math.Clamp(budget.DiversityLambda, 0.0, 1.0);
		Double lexicalWeight = Math.Clamp(budget.LexicalWeight, 0.0, 1.0);

		IReadOnlySet<String> queryTerms = TermSet(query);

		// Blended relevance and word set, computed once per candidate rather than once per MMR round.
		List<Scored> remaining = new(candidates.Count);

		foreach (RetrievalCandidate candidate in candidates)
		{
			IReadOnlySet<String> terms = TermSet(candidate.Text);
			Double semantic = Math.Clamp(candidate.Hit.Score, 0.0, 1.0);
			Double lexical = LexicalCoverage(queryTerms, terms);

			remaining.Add(new Scored(
				candidate,
				terms,
				((1.0 - lexicalWeight) * semantic) + (lexicalWeight * lexical)));
		}

		List<ReducedPassage> selected = new(Math.Min(maxPassages, remaining.Count));

		// The word sets of what has been selected, kept alongside so similarity does not need to look a
		// passage back up among the candidates.
		List<IReadOnlySet<String>> selectedTerms = new(selected.Capacity);

		Int32 usedTokens = 0;

		while (selected.Count < maxPassages && remaining.Count > 0)
		{
			// The highest MMR score: relevance, discounted by resemblance to what is already selected.
			Int32 bestIndex = 0;
			Double bestMmr = Double.NegativeInfinity;

			for (Int32 i = 0; i < remaining.Count; i++)
			{
				Double similarity = MaxSimilarityTo(remaining[i].Terms, selectedTerms);
				Double mmr = (lambda * remaining[i].Relevance) - ((1.0 - lambda) * similarity);

				if (mmr > bestMmr)
				{
					bestMmr = mmr;
					bestIndex = i;
				}
			}

			Scored best = remaining[bestIndex];
			remaining.RemoveAt(bestIndex);

			Boolean fits = usedTokens + best.Candidate.TokenCount <= maxTokens;

			// The first passage is admitted even if it alone exceeds the budget: some context beats none,
			// and an empty result is indistinguishable from having found nothing. After that, one that
			// would overflow is skipped and the scan continues — a later, smaller passage may still fit.
			if (fits || selected.Count == 0)
			{
				selected.Add(new ReducedPassage(best.Candidate.Hit, best.Candidate.Text, best.Relevance));
				selectedTerms.Add(best.Terms);

				usedTokens += best.Candidate.TokenCount;
			}
		}

		return selected;
	}

	/// <summary>The closest resemblance between a candidate and anything already selected; zero if none is.</summary>
	private static Double MaxSimilarityTo(
		IReadOnlySet<String> candidateTerms,
		List<IReadOnlySet<String>> selectedTerms)
	{
		Double maxSimilarity = 0.0;

		foreach (IReadOnlySet<String> terms in selectedTerms)
		{
			Double similarity = Jaccard(candidateTerms, terms);

			if (similarity > maxSimilarity)
			{
				maxSimilarity = similarity;
			}
		}

		return maxSimilarity;
	}

	/// <summary>
	/// The fraction of the query's distinct terms the passage contains. Zero when the query has no terms,
	/// which leaves the semantic score carrying the whole relevance rather than dividing by nothing.
	/// </summary>
	private static Double LexicalCoverage(IReadOnlySet<String> queryTerms, IReadOnlySet<String> passageTerms)
	{
		if (queryTerms.Count == 0)
		{
			return 0.0;
		}

		return (Double)queryTerms.Count(passageTerms.Contains) / queryTerms.Count;
	}

	private static Double Jaccard(IReadOnlySet<String> a, IReadOnlySet<String> b)
	{
		Int32 intersection = a.Count(b.Contains);
		Int32 union = a.Count + b.Count - intersection;

		return union == 0 ? 0.0 : (Double)intersection / union;
	}

	/// <summary>
	/// The lowercased word set used for lexical scoring and for diversity: split on any non-alphanumeric
	/// character, and drop single characters.
	///
	/// <para>
	/// Deliberately simple, and stopword-free for now. The signal is coarse term overlap and it carries a
	/// minority share of the ranking, so a stopword list would be tuning a weight that mostly is not
	/// deciding anything. It is recorded as deferred rather than forgotten.
	/// </para>
	/// </summary>
	private static IReadOnlySet<String> TermSet(String text)
	{
		HashSet<String> terms = new(StringComparer.Ordinal);

		if (String.IsNullOrEmpty(text))
		{
			return terms;
		}

		Int32 start = -1;

		for (Int32 i = 0; i <= text.Length; i++)
		{
			Boolean isWordChar = i < text.Length && Char.IsLetterOrDigit(text[i]);

			if (isWordChar)
			{
				if (start < 0)
				{
					start = i;
				}

				continue;
			}

			if (start >= 0)
			{
				if (i - start > 1)
				{
					terms.Add(text[start..i].ToLowerInvariant());
				}

				start = -1;
			}
		}

		return terms;
	}

	private sealed record Scored(RetrievalCandidate Candidate, IReadOnlySet<String> Terms, Double Relevance);
}
