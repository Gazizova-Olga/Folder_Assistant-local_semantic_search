using FluentAssertions;
using FolderAssistant.Retrieval;

namespace FolderAssistant.Tests;

/// <summary>
/// The low-confidence screen: what it refuses, what it lets through, and the three outcomes it has to
/// keep apart.
/// </summary>
public sealed class RelevanceFloorTests
{
	[Fact]
	public void A_Set_Whose_Best_Match_Clears_The_Floor_Passes_Through_Whole()
	{
		RetrievalCandidate[] candidates = [Candidate("a", 0.61), Candidate("b", 0.12)];

		ConfidenceScreening screening = RelevanceFloor.Screen(candidates, 0.4);

		screening.IsBelowFloor.Should().BeFalse();
		screening.BestScore.Should().BeApproximately(0.61, 1e-9);

		// Not thinned to the candidates that individually clear it. Whether a weak candidate is worth
		// keeping is a separate question, and one MinScore has already answered by this point.
		screening.Candidates.Should().BeSameAs(candidates);
	}

	[Fact]
	public void A_Set_Whose_Best_Match_Falls_Short_Is_Refused_Entirely()
	{
		RetrievalCandidate[] candidates = [Candidate("a", 0.19), Candidate("b", 0.03)];

		ConfidenceScreening screening = RelevanceFloor.Screen(candidates, 0.4);

		screening.IsBelowFloor.Should().BeTrue();
		screening.Candidates.Should().BeEmpty();
	}

	/// <summary>
	/// The score survives the refusal. A set turned away at 0.39 and one turned away at 0.01 describe
	/// different corpora, and an operator choosing a floor needs to see which one they have.
	/// </summary>
	[Fact]
	public void A_Refused_Set_Still_Reports_How_Close_It_Came()
	{
		ConfidenceScreening screening = RelevanceFloor.Screen([Candidate("a", 0.39)], 0.4);

		screening.IsBelowFloor.Should().BeTrue();
		screening.BestScore.Should().BeApproximately(0.39, 1e-9);
	}

	/// <summary>
	/// Finding nothing is not finding nothing good. Reporting an empty corpus as low confidence would tell
	/// a caller to rephrase a question that was fine, and hide that there was nothing to search.
	/// </summary>
	[Fact]
	public void An_Empty_Set_Is_Not_A_Low_Confidence_Result()
	{
		ConfidenceScreening screening = RelevanceFloor.Screen([], 0.4);

		screening.IsBelowFloor.Should().BeFalse();
		screening.Candidates.Should().BeEmpty();
		screening.BestScore.Should().Be(Double.NaN);
	}

	/// <summary>
	/// The default, and the state every profile is in until someone measures a floor for its embedder.
	/// </summary>
	[Fact]
	public void The_Floor_Off_Refuses_Nothing()
	{
		RetrievalCandidate[] candidates = [Candidate("a", 0.004)];

		ConfidenceScreening screening = RelevanceFloor.Screen(candidates, RelevanceFloor.Off);

		screening.IsBelowFloor.Should().BeFalse();
		screening.Candidates.Should().BeSameAs(candidates);
	}

	/// <summary>
	/// A floor is chosen from a measured distribution, so the value picked is usually one that was
	/// observed. Rejecting at exactly that value would exclude the observation it was chosen from.
	/// </summary>
	[Fact]
	public void A_Best_Match_Exactly_On_The_Floor_Is_Admitted()
	{
		RelevanceFloor.Screen([Candidate("a", 0.4)], 0.4).IsBelowFloor.Should().BeFalse();
	}

	/// <summary>
	/// Ranked order is the calling convention, not this contract's promise. Reading the first candidate as
	/// the best would refuse a qualifying set that arrived in any other order — silently, since the result
	/// is indistinguishable from a genuinely weak one.
	/// </summary>
	[Fact]
	public void The_Strongest_Candidate_Decides_Wherever_It_Sits_In_The_Set()
	{
		RetrievalCandidate[] unordered = [Candidate("a", 0.11), Candidate("b", 0.87), Candidate("c", 0.30)];

		ConfidenceScreening screening = RelevanceFloor.Screen(unordered, 0.4);

		screening.IsBelowFloor.Should().BeFalse();
		screening.BestScore.Should().BeApproximately(0.87, 1e-9);
	}

	private static RetrievalCandidate Candidate(String chunkId, Double score)
		=> new(new RetrievalHit(chunkId, $"{chunkId}.md", 0, 0, 10, score), $"passage {chunkId}", TokenCount: 10);
}
