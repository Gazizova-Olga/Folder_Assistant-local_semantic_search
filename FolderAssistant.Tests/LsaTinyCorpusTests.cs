using FluentAssertions;
using FolderAssistant.Embedding;
using FolderAssistant.Embedding.Lsa;

namespace FolderAssistant.Tests;

/// <summary>
/// The corpus-fitted embedder is the default, so the folders a default runs over include the smallest
/// ones: a single note, two files that share no word. The pruning floor — a term must appear in two
/// chunks — would leave such a corpus with no vocabulary and fail the index of a folder that has text in
/// it. Below the floor the vocabulary is kept whole and the fit is lexical; it is a fit, not a failure.
/// </summary>
public sealed class LsaTinyCorpusTests
{
	[Fact]
	public async Task A_Single_Chunk_Fits_And_Embeds()
	{
		LsaEmbeddingVectorizer vectorizer = LsaEmbeddingVectorizer.CreateForFitting("tiny-v1", targetDimension: 8);

		String artifact = vectorizer.Fit(["alpha beta gamma"]);
		IReadOnlyList<EmbeddingResult> query = await vectorizer.VectorizeAsync(["alpha"], EmbeddingKind.Query);

		artifact.Should().NotBeNullOrWhiteSpace();
		vectorizer.Descriptor.Dimension.Should().BeGreaterThan(0);
		query.Should().ContainSingle().Which.Vector.Should().HaveCount(vectorizer.Descriptor.Dimension);
	}

	[Fact]
	public async Task Two_Chunks_Sharing_No_Term_Fit_And_Keep_Them_Apart()
	{
		LsaEmbeddingVectorizer vectorizer = LsaEmbeddingVectorizer.CreateForFitting("tiny-v1", targetDimension: 8);

		vectorizer.Fit(["chocolate cake recipe with butter", "diesel engine torque and pistons"]);

		IReadOnlyList<EmbeddingResult> embedded = await vectorizer.VectorizeAsync(
			["butter cake", "engine pistons"], EmbeddingKind.Query);
		Single[] cake = [.. embedded[0].Vector];
		Single[] engine = [.. embedded[1].Vector];

		Cosine(cake, cake).Should().BeApproximately(1f, 1e-3f);
		Cosine(cake, engine).Should().BeLessThan(Cosine(cake, cake));
	}

	[Fact]
	public void An_Empty_Corpus_Still_Refuses_To_Fit()
	{
		LsaEmbeddingVectorizer vectorizer = LsaEmbeddingVectorizer.CreateForFitting("tiny-v1", targetDimension: 8);

		Action fit = () => vectorizer.Fit([]);

		fit.Should().Throw<ArgumentException>();
	}

	private static Single Cosine(Single[] a, Single[] b)
	{
		Single dot = 0;
		Single na = 0;
		Single nb = 0;
		for (Int32 i = 0; i < a.Length; i++)
		{
			dot += a[i] * b[i];
			na += a[i] * a[i];
			nb += b[i] * b[i];
		}

		return na <= 0 || nb <= 0 ? 0 : dot / (MathF.Sqrt(na) * MathF.Sqrt(nb));
	}
}
