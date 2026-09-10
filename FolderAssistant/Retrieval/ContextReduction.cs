namespace FolderAssistant.Retrieval;

/// <summary>
/// A retrieval candidate with its passage text resolved and the token length of its chunk span — the
/// input to context reduction.
///
/// <para>
/// Retrieval itself returns only a location and a score: chunks store no text (`SPEC-120`), so whoever
/// consumes a hit resolves the passage before handing candidates here. The token count travels with it
/// because the budget is spent in the same units the chunker counted in.
/// </para>
/// </summary>
internal sealed record RetrievalCandidate(RetrievalHit Hit, String Text, Int32 TokenCount);

/// <summary>
/// A candidate the reducer selected, carrying the blended relevance that ordered it. That score is
/// deliberately not the raw cosine on <see cref="RetrievalHit.Score"/> — it is what the reranking
/// actually used, and reporting the cosine instead would explain the wrong ordering.
/// </summary>
internal sealed record ReducedPassage(RetrievalHit Hit, String Text, Double Score);

/// <summary>Budget and weights governing context assembly.</summary>
/// <param name="MaxPassages">Hard cap on how many passages may be selected.</param>
/// <param name="MaxTokens">Token budget across the selected passages, in chunk tokens.</param>
/// <param name="LexicalWeight">
/// Share of the blended relevance taken from lexical query-term coverage; the rest is the semantic score.
/// Modest by default, so semantics still lead but a passage containing the exact keyword can overtake a
/// near-tied paraphrase.
/// </param>
/// <param name="DiversityLambda">
/// The MMR trade-off between relevance and novelty. <c>1.0</c> is pure relevance with no diversity
/// penalty; lower values penalise a candidate that repeats what is already selected.
/// </param>
internal sealed record ContextBudget(
	Int32 MaxPassages,
	Int32 MaxTokens,
	Double LexicalWeight = 0.3,
	Double DiversityLambda = 0.7);

/// <summary>
/// The rerank and token-budget stage that sits between retrieval and the model.
///
/// <para>
/// Raw top-k is not context. The k best passages by cosine are frequently near-duplicates of each other —
/// the same paragraph, chunked twice, or the same idea said twice in one file — and handing all of them
/// over spends the whole budget on one idea while the answer sits in the passage that came ninth. This
/// stage re-orders by a hybrid relevance and then selects greedily for diversity under a budget.
/// </para>
///
/// <para>
/// It is a separate stage over <see cref="IRetrievalQuery"/>'s output rather than a change to that
/// contract, because ranking and budgeting answer different questions: what matches, and what is worth
/// spending the context on. A backend that ranks well should not also have to know a token budget.
/// </para>
/// </summary>
internal interface IContextReduction
{
	/// <summary>
	/// Reranks and budget-selects <paramref name="candidates"/> for <paramref name="query"/>.
	///
	/// <para>
	/// Always returns at least the single most relevant candidate when any were supplied, even if that one
	/// alone exceeds the token budget: a passage too large to fit is still better context than none, and
	/// returning nothing would look identical to finding nothing.
	/// </para>
	/// </summary>
	IReadOnlyList<ReducedPassage> Reduce(
		String query,
		IReadOnlyList<RetrievalCandidate> candidates,
		ContextBudget budget);
}
