namespace FolderAssistant.Retrieval;

/// <summary>
/// What screening a candidate set against the relevance floor concluded.
///
/// <para>
/// Three outcomes have to stay distinguishable to whoever asked, and collapsing any two of them loses
/// something a caller needs: nothing was found; things were found but none of them are good enough to
/// answer from; things were found and they are worth reading. The middle one is the reason this type
/// exists — without it, it arrives as either of its neighbours.
/// </para>
/// </summary>
/// <param name="Candidates">
/// What survived screening: the original set, or empty when the whole set was rejected.
/// </param>
/// <param name="BestScore">
/// The strongest raw retrieval score the set contained, or <see cref="Double.NaN"/> when it was empty.
/// Reported whether or not the set was rejected, because "rejected at 0.19" and "rejected at 0.02" are
/// different situations and only the number separates them.
/// </param>
/// <param name="IsBelowFloor">
/// Whether the set was rejected for lack of confidence. <see langword="false"/> for an empty set: finding
/// nothing is not the same as finding nothing good, and an empty corpus would otherwise report itself as
/// a low-confidence answer.
/// </param>
internal sealed record ConfidenceScreening(
	IReadOnlyList<RetrievalCandidate> Candidates,
	Double BestScore,
	Boolean IsBelowFloor);

/// <summary>
/// The low-confidence short circuit: drops a whole candidate set when even its best match is too weak to
/// be worth putting in front of a model.
///
/// <para>
/// This is a different decision from <see cref="RetrievalOptions.MinScore"/>, which both backends already
/// apply per candidate while ranking. A per-candidate filter thins a set; this one refuses it. The
/// distinction matters because a handful of weak passages is worse than none — they cost context tokens
/// and they invite an answer built on text that does not address the question, which reads exactly like
/// an answer built on text that does.
/// </para>
///
/// <para>
/// It sits over <see cref="IRetrievalQuery"/>'s output and before <see cref="IContextReduction"/>, and it
/// has to: the reduction contract promises to return the best candidate it was given even when that
/// candidate alone busts the budget, so a stage that must sometimes return nothing cannot live inside it.
/// </para>
///
/// <para>
/// <strong>The floor is a property of the embedding model, not of this application</strong>, which is why
/// there is no constant here and the default is <see cref="Off"/>. Measured with
/// <c>RelevanceFloorBenchmark</c> and written up in `SPEC-110`: with <c>qwen3-embedding:0.6b</c> the answerable and
/// unanswerable populations separate cleanly and any floor in <c>(0.2520, 0.5040]</c> divides them, while
/// with the placeholder embedder they overlap and **no floor divides them at all** — the highest-scoring
/// question of the whole set is one the corpus cannot answer. A shipped non-zero default would therefore
/// claim a filtering power the default profile's scores do not have, which is the reason
/// <see cref="RetrievalOptions.MinScore"/> defaults to zero as well.
/// </para>
/// </summary>
internal static class RelevanceFloor
{
	/// <summary>
	/// No screening. The default, and the only honest one while the floor that applies depends on which
	/// embedder a profile composed.
	/// </summary>
	public const Double Off = 0.0;

	/// <summary>
	/// Screens <paramref name="candidates"/> against <paramref name="floor"/>, on the strongest score in
	/// the set rather than on each candidate.
	///
	/// <para>
	/// The best match is what decides, because it is what says whether the corpus has anything to say at
	/// all. Screening candidate by candidate would answer a different question — which of these is worth
	/// keeping — and that one already has an answer upstream of here.
	/// </para>
	/// </summary>
	public static ConfidenceScreening Screen(IReadOnlyList<RetrievalCandidate> candidates, Double floor)
	{
		ArgumentNullException.ThrowIfNull(candidates);

		if (candidates.Count == 0)
		{
			return new ConfidenceScreening(candidates, Double.NaN, IsBelowFloor: false);
		}

		// Not candidates[0]: the ranked order is the caller's convention, not this contract's, and reading
		// a set that arrived unordered as though it were ranked would reject on an arbitrary member.
		Double best = candidates.Max(candidate => candidate.Hit.Score);

		// Strictly below, so a floor set exactly at an observed score admits it. A floor is chosen from a
		// measured distribution, and the value picked is normally one that was seen.
		return best < floor
			? new ConfidenceScreening([], best, IsBelowFloor: true)
			: new ConfidenceScreening(candidates, best, IsBelowFloor: false);
	}
}
