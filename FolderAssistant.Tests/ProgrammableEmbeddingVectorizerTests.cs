using FluentAssertions;
using FolderAssistant.Embedding;

namespace FolderAssistant.Tests;

public sealed class ProgrammableEmbeddingVectorizerTests
{
	[Fact]
	public void The_Same_Text_Always_Embeds_To_The_Same_Vector()
	{
		ProgrammableEmbeddingVectorizer vectorizer = new("m1", 32);

		vectorizer.Vectorize("alpha beta", EmbeddingKind.Query).Vector
			.Should().Equal(vectorizer.Vectorize("alpha beta", EmbeddingKind.Query).Vector);
	}

	/// <summary>
	/// The floor this embedder sets, stated as a test rather than only in prose: a character histogram
	/// cannot tell an anagram apart, which is the whole reason a corpus-fitted implementation is worth
	/// building. If this ever fails, the baseline has stopped being the baseline.
	/// </summary>
	[Fact]
	public void It_Cannot_Tell_An_Anagram_Apart()
	{
		ProgrammableEmbeddingVectorizer vectorizer = new("m1", 32);

		vectorizer.Vectorize("dog", EmbeddingKind.Document).Vector
			.Should().Equal(vectorizer.Vectorize("god", EmbeddingKind.Document).Vector);
	}

	/// <summary>
	/// A symmetric implementation may ignore the kind, and this one does — pinned so that a later
	/// asymmetric implementation changing its own behaviour here is a deliberate act, not a surprise.
	/// </summary>
	[Fact]
	public void Document_And_Query_Embed_Identically_For_This_Implementation()
	{
		ProgrammableEmbeddingVectorizer vectorizer = new("m1", 16);

		vectorizer.Vectorize("alpha", EmbeddingKind.Document).Vector
			.Should().Equal(vectorizer.Vectorize("alpha", EmbeddingKind.Query).Vector);
	}

	[Fact]
	public void A_Batch_Embeds_Each_Text_As_It_Would_Alone()
	{
		ProgrammableEmbeddingVectorizer vectorizer = new("m1", 16);

		IReadOnlyList<EmbeddingResult> batch =
			vectorizer.Vectorize(["alpha", "beta", "gamma"], EmbeddingKind.Document);

		batch.Should().HaveCount(3);
		batch[1].Vector.Should().Equal(vectorizer.Vectorize("beta", EmbeddingKind.Document).Vector);
	}

	[Fact]
	public void The_Descriptor_Reports_The_Dimension_And_Every_Vector_Matches_It()
	{
		ProgrammableEmbeddingVectorizer vectorizer = new("m1", 8);

		vectorizer.Descriptor.Dimension.Should().Be(8);
		vectorizer.Descriptor.ProviderType.Should().Be("programmable");
		vectorizer.Descriptor.ModelVersionId.Should().Be("m1");
		vectorizer.Descriptor.DistanceMetric.Should().Be("cosine");

		EmbeddingResult result = vectorizer.Vectorize("alpha", EmbeddingKind.Document);
		result.Vector.Should().HaveCount(8);
		result.Dimension.Should().Be(8);
	}

	[Fact]
	public void A_Vector_Is_Unit_Length()
	{
		Single[] vector = new ProgrammableEmbeddingVectorizer("m1", 16)
			.Vectorize("alpha beta gamma", EmbeddingKind.Document).Vector.ToArray();

		Math.Sqrt(vector.Sum(static component => (Double)component * component)).Should().BeApproximately(1.0, 1e-6);
	}

	[Fact]
	public void Text_With_Nothing_In_It_Embeds_To_Zero_Rather_Than_Failing()
		=> new ProgrammableEmbeddingVectorizer("m1", 8)
			.Vectorize("   ", EmbeddingKind.Document).Vector
			.Should().Equal(new Single[8]);

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	public void A_Dimension_That_Is_Not_Positive_Is_Refused(Int32 dimension)
		=> FluentActions.Invoking(() => new ProgrammableEmbeddingVectorizer("m1", dimension))
			.Should().Throw<ArgumentOutOfRangeException>();

	[Fact]
	public void A_Cancelled_Batch_Stops()
	{
		using CancellationTokenSource cancelled = new();
		cancelled.Cancel();

		FluentActions.Invoking(() => new ProgrammableEmbeddingVectorizer("m1", 8)
				.Vectorize(["alpha"], EmbeddingKind.Document, cancelled.Token))
			.Should().Throw<OperationCanceledException>();
	}
}
