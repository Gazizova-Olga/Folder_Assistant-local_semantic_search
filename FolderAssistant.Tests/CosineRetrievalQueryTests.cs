using FluentAssertions;
using FolderAssistant.Embedding;
using FolderAssistant.Persistence;
using FolderAssistant.Retrieval;

namespace FolderAssistant.Tests;

public sealed class CosineRetrievalQueryTests
{
	[Fact]
	public void The_Closest_Vector_Ranks_First()
	{
		FakeVectorStoreReader store = new(
			Stored("c1", [1.0f, 0.0f, 0.0f]),
			Stored("c2", [0.0f, 1.0f, 0.0f]),
			Stored("c3", [0.9f, 0.1f, 0.0f]));

		IReadOnlyList<RetrievalHit> hits = Search(store, [1.0f, 0.0f, 0.0f]);

		hits.Should().HaveCount(3);
		hits[0].ChunkId.Should().Be("c1");
		hits[1].ChunkId.Should().Be("c3");
		hits[2].ChunkId.Should().Be("c2");
	}

	/// <summary>
	/// A hit has to be locatable. A chunk stores no text of its own, so retrieval carries the file and
	/// the token window that let the passage be rebuilt — a score with no source is not a result.
	/// </summary>
	[Fact]
	public void A_Hit_Carries_Where_Its_Text_Came_From()
	{
		FakeVectorStoreReader store = new(
			new StoredChunkVector("c1", "notes/a.md", 3, 12, 20, [1.0f, 0.0f]));

		RetrievalHit hit = Search(store, [1.0f, 0.0f])[0];

		hit.FilePath.Should().Be("notes/a.md");
		hit.ChunkIndex.Should().Be(3);
		hit.TokenStart.Should().Be(12);
		hit.TokenEnd.Should().Be(20);
	}

	/// <summary>
	/// Vectors from a different embedding model occupy a different space, so a similarity computed
	/// across them is a number with no meaning. Retrieval asks the store for one model version, and a
	/// version that was never indexed returns nothing rather than falling back to another model's rows.
	/// </summary>
	[Fact]
	public void Only_The_Active_Model_Versions_Vectors_Are_Considered()
	{
		FakeVectorStoreReader store = new(Stored("c1", [1.0f, 0.0f]));

		IReadOnlyList<RetrievalHit> hits = new CosineRetrievalQuery(new StubVectorizer([1.0f, 0.0f], "other-model"), store)
			.Search("db", "anything", new RetrievalOptions());

		store.RequestedModelVersions.Should().Equal("other-model");
		hits.Should().BeEmpty();
	}

	[Fact]
	public void Nothing_Below_The_Minimum_Score_Is_Returned()
	{
		FakeVectorStoreReader store = new(
			Stored("close", [1.0f, 0.0f]),
			Stored("orthogonal", [0.0f, 1.0f]));

		IReadOnlyList<RetrievalHit> hits = Search(store, [1.0f, 0.0f], new RetrievalOptions(MinScore: 0.5));

		hits.Should().ContainSingle();
		hits[0].ChunkId.Should().Be("close");
	}

	[Fact]
	public void No_More_Than_TopK_Hits_Come_Back()
	{
		FakeVectorStoreReader store = new(
			Stored("c1", [1.0f, 0.0f]),
			Stored("c2", [0.9f, 0.1f]),
			Stored("c3", [0.8f, 0.2f]));

		Search(store, [1.0f, 0.0f], new RetrievalOptions(TopK: 2)).Should().HaveCount(2);
	}

	/// <summary>
	/// Two chunks scoring identically must not swap places between runs, or a comparison against a
	/// second backend would report a difference that is not one. The tie breaks on chunk id.
	/// </summary>
	[Fact]
	public void Tied_Scores_Break_On_Chunk_Id_So_The_Order_Is_Repeatable()
	{
		FakeVectorStoreReader store = new(
			Stored("zebra", [1.0f, 0.0f]),
			Stored("alpha", [1.0f, 0.0f]));

		Search(store, [1.0f, 0.0f]).Select(static hit => hit.ChunkId).Should().Equal("alpha", "zebra");
	}

	/// <summary>
	/// Same model version, different dimension: the stored vectors were written by a build that
	/// disagreed with this one. Scoring them would return plausible nonsense, so it says so instead.
	/// </summary>
	[Fact]
	public void A_Stored_Vector_Of_The_Wrong_Dimension_Is_Refused_Rather_Than_Scored()
	{
		FakeVectorStoreReader store = new(Stored("c1", [1.0f, 0.0f, 0.0f]));

		FluentActions.Invoking(() => Search(store, [1.0f, 0.0f]))
			.Should().Throw<InvalidOperationException>()
			.WithMessage("*must be rebuilt*");
	}

	[Fact]
	public void A_Zero_Vector_Scores_Zero_Rather_Than_Dividing_By_Zero()
	{
		FakeVectorStoreReader store = new(Stored("c1", [0.0f, 0.0f]));

		Search(store, [1.0f, 0.0f], new RetrievalOptions(MinScore: Double.NegativeInfinity))[0]
			.Score.Should().Be(0.0);
	}

	[Fact]
	public void An_Empty_Index_Returns_Nothing()
		=> Search(new FakeVectorStoreReader(), [1.0f, 0.0f]).Should().BeEmpty();

	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	public void A_TopK_That_Is_Not_Positive_Is_Refused(Int32 topK)
		=> FluentActions.Invoking(() => Search(new FakeVectorStoreReader(), [1.0f], new RetrievalOptions(TopK: topK)))
			.Should().Throw<ArgumentOutOfRangeException>();

	/// <summary>An asymmetric model treats the two sides differently; a query must be embedded as one.</summary>
	[Fact]
	public void The_Query_Is_Embedded_As_A_Query_Not_As_A_Document()
	{
		StubVectorizer vectorizer = new([1.0f, 0.0f]);

		new CosineRetrievalQuery(vectorizer, new FakeVectorStoreReader())
			.Search("db", "find me", new RetrievalOptions());

		vectorizer.Kinds.Should().Equal(EmbeddingKind.Query);
	}

	[Fact]
	public void A_Query_Cannot_Be_Built_Without_A_Vectorizer_Or_A_Reader()
	{
		FluentActions.Invoking(() => new CosineRetrievalQuery(null!)).Should().Throw<ArgumentNullException>();
		// Cast because the state argument is optional: an untyped null would pick that overload.
		FluentActions.Invoking(() => new CosineRetrievalQuery(new StubVectorizer([1.0f]), (IVectorStoreReader)null!))
			.Should().Throw<ArgumentNullException>();
	}

	private static IReadOnlyList<RetrievalHit> Search(
		IVectorStoreReader store,
		Single[] queryVector,
		RetrievalOptions? options = null)
		=> new CosineRetrievalQuery(new StubVectorizer(queryVector), store)
			.Search("db", "query text", options ?? new RetrievalOptions());

	private static StoredChunkVector Stored(String chunkId, Single[] vector)
		=> new(chunkId, $"{chunkId}.md", 0, 0, 1, vector);

	/// <summary>
	/// What the reader used to return in one call: a vector and its location together. Kept as a
	/// test-side shape so these tests still read as "here is a stored chunk", while the production
	/// contract splits the two.
	/// </summary>
	private sealed record StoredChunkVector(
		String ChunkId,
		String FilePath,
		Int32 ChunkIndex,
		Int32 TokenStart,
		Int32 TokenEnd,
		IReadOnlyList<Single> Vector);

	private sealed class StubVectorizer(Single[] vector, String modelVersionId = "m1") : IVectorizer
	{
		public List<EmbeddingKind> Kinds { get; } = [];

		public ModelDescriptor Descriptor { get; }
			= new(modelVersionId, "stub", "stub-model", vector.Length);

		public IReadOnlyList<EmbeddingResult> Vectorize(
			IReadOnlyList<String> texts,
			EmbeddingKind kind,
			CancellationToken cancellationToken = default)
		{
			this.Kinds.Add(kind);

			return texts
				.Select(_ => new EmbeddingResult(vector, modelVersionId, vector.Length, "stub"))
				.ToArray();
		}
	}

	private sealed class FakeVectorStoreReader(params StoredChunkVector[] vectors) : IVectorStoreReader
	{
		public List<String> RequestedModelVersions { get; } = [];

		public IReadOnlyList<StoredVector> ReadVectorsByModelVersion(String databasePath, String modelVersionId)
		{
			this.RequestedModelVersions.Add(modelVersionId);

			return modelVersionId == "m1"
				? [.. vectors.Select(v => new StoredVector(v.ChunkId, v.Vector))]
				: [];
		}

		public IReadOnlySet<String> ReadFileIdsWithVectors(String databasePath, String modelVersionId)
			=> new HashSet<String>(StringComparer.OrdinalIgnoreCase);

		public IReadOnlyDictionary<String, ChunkLocation> ReadChunkLocations(
			String databasePath,
			IReadOnlyList<String> chunkIds)
			=> vectors
				.Where(v => chunkIds.Contains(v.ChunkId, StringComparer.Ordinal))
				.ToDictionary(
					v => v.ChunkId,
					v => new ChunkLocation(v.ChunkId, v.FilePath, v.ChunkIndex, v.TokenStart, v.TokenEnd),
					StringComparer.Ordinal);

		public String? ReadFitArtifact(String databasePath, String modelVersionId) => null;
	}
}
