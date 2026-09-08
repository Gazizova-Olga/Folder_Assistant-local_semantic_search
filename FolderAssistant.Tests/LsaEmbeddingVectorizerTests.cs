using FluentAssertions;
using FolderAssistant.Embedding;
using FolderAssistant.Embedding.Lsa;

namespace FolderAssistant.Tests;

public sealed class LsaEmbeddingVectorizerTests
{
	/// <summary>
	/// A corpus where "car" and "automobile" never appear together, but appear in the same company:
	/// engine, wheel, drive, road. Nothing lexical connects the two words.
	/// </summary>
	private static readonly String[] SynonymyCorpus =
	[
		"the car has an engine and four wheels",
		"a car needs an engine to drive on the road",
		"my car engine wheels road drive",
		"the automobile has an engine and four wheels",
		"an automobile needs an engine to drive on the road",
		"my automobile engine wheels road drive",
		"bread flour yeast oven baking loaf",
		"the loaf of bread came from the oven after baking",
		"flour and yeast make bread in the oven",
	];

	/// <summary>
	/// The reason this implementation exists. "car" and "automobile" never co-occur, so no lexical
	/// method can relate them — but they occupy the same contexts, and the reduction is what turns
	/// that into similarity. The baseline is asserted <em>not</em> to manage it, so the comparison
	/// pins what the fitting actually buys rather than just that it runs.
	/// </summary>
	[Fact]
	public void A_Fitted_Model_Relates_Words_That_Never_Co_Occur_And_The_Baseline_Does_Not()
	{
		Double fitted = SimilarityUnder(Fitted(k: 2), "car", "automobile");
		Double baseline = SimilarityUnder(new ProgrammableEmbeddingVectorizer("m1", 32), "car", "automobile");

		fitted.Should().BeGreaterThan(0.9, "the two words share every context in the corpus");
		baseline.Should().BeLessThan(0.2, "a character histogram has no way to relate them");
	}

	[Fact]
	public void An_Unrelated_Topic_Stays_Unrelated()
		=> SimilarityUnder(Fitted(k: 2), "car", "bread")
			.Should().BeLessThan(SimilarityUnder(Fitted(k: 2), "car", "automobile"));

	/// <summary>
	/// The compression <em>is</em> the mechanism, and k has a usable band with a failure on each side.
	/// Measured on this nine-document corpus (rank 8):
	///
	/// <code>
	/// k = 1   car/automobile 1.000   car/bread 1.000   -- degenerate: everything is one concept
	/// k = 2   car/automobile 0.988   car/bread 0.185
	/// k = 3   car/automobile 0.871   car/bread 0.185
	/// k = 4   car/automobile -0.514  car/bread 0.126   -- collapsed
	/// k = 8   car/automobile -0.577  car/bread 0.108
	/// </code>
	///
	/// <para>
	/// Both ends fail silently. Too low and every document is similar to every other; too high and
	/// the synonymy disappears entirely. Nothing throws in either case — retrieval just quietly
	/// returns worse answers, which is why this is asserted rather than described.
	/// </para>
	/// </summary>
	[Fact]
	public void Similarity_Collapses_When_K_Approaches_The_Corpus_Rank()
	{
		SimilarityUnder(Fitted(k: 2), "car", "automobile").Should().BeGreaterThan(0.9);
		SimilarityUnder(Fitted(k: 3), "car", "automobile").Should().BeGreaterThan(0.8);
		SimilarityUnder(Fitted(k: 4), "car", "automobile").Should().BeLessThan(0.0);
		SimilarityUnder(Fitted(k: 8), "car", "automobile").Should().BeLessThan(0.0);
	}

	/// <summary>
	/// The other end of the band, and the reason "smaller k is safer" is wrong: at k = 1 there is a
	/// single concept, so every document resembles every other and the model discriminates nothing.
	/// </summary>
	[Fact]
	public void A_K_Of_One_Makes_Everything_Similar_To_Everything()
	{
		SimilarityUnder(Fitted(k: 1), "car", "bread").Should().BeGreaterThan(0.9);
		SimilarityUnder(Fitted(k: 2), "car", "bread").Should().BeLessThan(0.3);
	}

	/// <summary>
	/// The projection scales by <c>1/√λ</c>, which is <c>1/Σ</c> — not by <c>1/λ</c>. Dividing by λ
	/// cancels Σ out of the transform entirely, so a weak noise concept is weighted exactly as
	/// heavily as the dominant one.
	///
	/// <para>
	/// This test is what pins that, and the threshold below is the reason it is a separate test
	/// rather than a comment. Measured on this corpus, at k = 3: <b>0.871 with 1/√λ, 0.701 with
	/// 1/λ</b>. Note how narrow that is — at k = 2 the two are 0.988 and 0.981, indistinguishable.
	/// So the scaling error is only visible in a band, and a corpus or a k chosen slightly
	/// differently would not catch it at all.
	/// </para>
	/// </summary>
	[Fact]
	public void The_Projection_Scales_By_The_Square_Root_Of_The_Eigenvalue()
		=> SimilarityUnder(Fitted(k: 3), "car", "automobile")
			.Should().BeGreaterThan(0.8, "dividing by lambda rather than its square root gives 0.70 here");

	[Fact]
	public void Vectorizing_Before_Fitting_Throws_Rather_Than_Returning_A_Poor_Vector()
		=> FluentActions.Invoking(() => LsaEmbeddingVectorizer
				.CreateForFitting("m1", 4)
				.Vectorize("anything", EmbeddingKind.Document))
			.Should().Throw<InvalidOperationException>()
			.WithMessage("*not fitted*");

	/// <summary>
	/// A query must be embedded by the same fit that produced the indexed vectors. Restoring from the
	/// artifact has to reproduce the original transform exactly, or retrieval compares across spaces.
	///
	/// <para>
	/// This is also what pins the analyzer configuration coming from the artifact rather than from the
	/// caller — but only in one direction, and the asymmetry is worth knowing. If a restore ignored the
	/// stored setting and analyzed <em>without</em> trigrams, the trigram columns in the vocabulary
	/// would never be counted and this test fails. If it analyzed <em>with</em> trigrams against a
	/// vocabulary fitted without them, the extra terms are simply not in the index and are dropped, so
	/// the vectors are genuinely identical. That direction is harmless and consequently untestable;
	/// there is no assertion for it, deliberately.
	/// </para>
	/// </summary>
	[Fact]
	public void A_Model_Restored_From_Its_Artifact_Embeds_Identically()
	{
		LsaEmbeddingVectorizer original = LsaEmbeddingVectorizer.CreateForFitting("m1", 3);
		String artifact = original.Fit(SynonymyCorpus);

		LsaEmbeddingVectorizer restored = LsaEmbeddingVectorizer.FromArtifact("m1", artifact);

		restored.Vectorize("car engine road", EmbeddingKind.Query).Vector
			.Should().Equal(original.Vectorize("car engine road", EmbeddingKind.Query).Vector);
	}

	/// <summary>The stored dimension is what a restored model reports, not a value from the caller.</summary>
	[Fact]
	public void A_Restored_Model_Reports_The_Dimension_Recorded_In_Its_Artifact()
	{
		LsaEmbeddingVectorizer original = LsaEmbeddingVectorizer.CreateForFitting("m1", 3);
		String artifact = original.Fit(SynonymyCorpus);

		LsaEmbeddingVectorizer.FromArtifact("m1", artifact).Descriptor.Dimension
			.Should().Be(original.Descriptor.Dimension);
	}

	[Fact]
	public void The_Descriptor_Reports_The_Rank_Actually_Fitted_Not_The_One_Requested()
	{
		// Three tiny documents cannot support a rank of fifty.
		LsaEmbeddingVectorizer vectorizer = LsaEmbeddingVectorizer.CreateForFitting("m1", 50);
		vectorizer.Fit(["alpha beta gamma", "alpha beta delta", "alpha gamma delta"]);

		vectorizer.Descriptor.Dimension.Should().BeLessThan(50);
		vectorizer.Descriptor.ProviderType.Should().Be("programmable-lsa");
		vectorizer.Vectorize("alpha", EmbeddingKind.Document).Vector
			.Should().HaveCount(vectorizer.Descriptor.Dimension);
	}

	[Fact]
	public void Text_Of_Entirely_Unknown_Words_Embeds_To_Zero()
	{
		LsaEmbeddingVectorizer vectorizer = Fitted(k: 2);

		vectorizer.Vectorize("zzzz yyyy xxxx", EmbeddingKind.Query).Vector
			.Should().Equal(new Single[vectorizer.Descriptor.Dimension]);
	}

	[Fact]
	public void The_Same_Text_Always_Embeds_To_The_Same_Vector()
	{
		LsaEmbeddingVectorizer vectorizer = Fitted(k: 3);

		vectorizer.Vectorize("car engine", EmbeddingKind.Query).Vector
			.Should().Equal(vectorizer.Vectorize("car engine", EmbeddingKind.Query).Vector);
	}

	[Fact]
	public void Fitting_The_Same_Corpus_Twice_Produces_The_Same_Artifact()
	{
		LsaEmbeddingVectorizer.CreateForFitting("m1", 3).Fit(SynonymyCorpus)
			.Should().Be(LsaEmbeddingVectorizer.CreateForFitting("m1", 3).Fit(SynonymyCorpus));
	}

	[Fact]
	public void An_Empty_Corpus_Is_Refused()
		=> FluentActions.Invoking(() => LsaEmbeddingVectorizer.CreateForFitting("m1", 3).Fit([]))
			.Should().Throw<ArgumentException>();

	[Fact]
	public void A_Corpus_With_No_Usable_Terms_Is_Refused()
		=> FluentActions.Invoking(() => LsaEmbeddingVectorizer.CreateForFitting("m1", 3).Fit(["!!!", "???"]))
			.Should().Throw<InvalidOperationException>();

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	public void A_Dimension_That_Is_Not_Positive_Is_Refused(Int32 dimension)
		=> FluentActions.Invoking(() => LsaEmbeddingVectorizer.CreateForFitting("m1", dimension))
			.Should().Throw<ArgumentOutOfRangeException>();

	[Fact]
	public void A_Cancelled_Batch_Stops()
	{
		using CancellationTokenSource cancelled = new();
		cancelled.Cancel();

		FluentActions.Invoking(() => Fitted(k: 2).Vectorize(["car"], EmbeddingKind.Document, cancelled.Token))
			.Should().Throw<OperationCanceledException>();
	}

	private static LsaEmbeddingVectorizer Fitted(Int32 k)
	{
		LsaEmbeddingVectorizer vectorizer = LsaEmbeddingVectorizer.CreateForFitting("m1", k);
		vectorizer.Fit(SynonymyCorpus);

		return vectorizer;
	}

	private static Double SimilarityUnder(IVectorizer vectorizer, String left, String right)
		=> Cosine(
			vectorizer.Vectorize(left, EmbeddingKind.Query).Vector,
			vectorizer.Vectorize(right, EmbeddingKind.Query).Vector);

	private static Double Cosine(IReadOnlyList<Single> left, IReadOnlyList<Single> right)
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

		return leftNorm <= 0 || rightNorm <= 0 ? 0.0 : dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
	}
}
