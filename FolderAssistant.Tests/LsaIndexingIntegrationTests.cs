using System.Text;
using FluentAssertions;
using FolderAssistant.Embedding.Lsa;
using FolderAssistant.Indexing;
using FolderAssistant.Persistence;
using FolderAssistant.Retrieval;

namespace FolderAssistant.Tests;

/// <summary>
/// The corpus-fitted vectorizer through the real pipeline: fit during indexing, artifact persisted
/// with the vectors it produced, and a query embedded by a model rebuilt from that artifact.
/// </summary>
public sealed class LsaIndexingIntegrationTests
{
	[Fact]
	public void Indexing_With_A_Fittable_Vectorizer_Persists_Its_Fit()
	{
		using TempFolder folder = new();
		String databasePath = IndexWithLsa(folder);

		String? artifact = new SqliteBlobVectorStoreReader().ReadFitArtifact(databasePath, "lsa-v1");

		artifact.Should().NotBeNullOrWhiteSpace();
		artifact.Should().Contain("Projection");
	}

	/// <summary>
	/// The artifact and the vectors it produced go in together. An artifact that disagreed with the
	/// stored vectors would corrupt every query embedded against it, and nothing would report it.
	/// </summary>
	[Fact]
	public void The_Stored_Vectors_Match_The_Dimension_The_Fit_Actually_Reached()
	{
		using TempFolder folder = new();
		String databasePath = IndexWithLsa(folder);

		SqliteBlobVectorStoreReader reader = new();
		String artifact = reader.ReadFitArtifact(databasePath, "lsa-v1")!;
		Int32 fittedDimension = LsaEmbeddingVectorizer.FromArtifact("lsa-v1", artifact).Descriptor.Dimension;

		IReadOnlyList<StoredVector> stored = reader.ReadVectorsByModelVersion(databasePath, "lsa-v1");

		stored.Should().NotBeEmpty();
		stored.Should().OnlyContain(vector => vector.Vector.Count == fittedDimension);
	}

	/// <summary>
	/// The closing of the loop: index a folder, throw the fitted instance away, rebuild it from what
	/// was persisted, and retrieve.
	/// </summary>
	[Fact]
	public void A_Query_Embedded_From_The_Persisted_Fit_Retrieves_The_Indexed_Content()
	{
		using TempFolder folder = new();
		String databasePath = IndexWithLsa(folder);

		String artifact = new SqliteBlobVectorStoreReader().ReadFitArtifact(databasePath, "lsa-v1")!;

		IReadOnlyList<RetrievalHit> hits = new CosineRetrievalQuery(
				LsaEmbeddingVectorizer.FromArtifact("lsa-v1", artifact))
			.Search(databasePath, "engine and wheels", new RetrievalOptions(TopK: 3));

		hits.Should().NotBeEmpty();
		hits[0].Score.Should().BeGreaterThan(0.0);
		hits.Select(static hit => hit.Score).Should().BeInDescendingOrder();
	}

	/// <summary>
	/// The registry records what the fit actually produced, not what was asked for — otherwise the
	/// stored dimension and the recorded one disagree and nothing notices until a query fails.
	/// </summary>
	[Fact]
	public void The_Registry_Records_The_Fitted_Provider_And_Dimension()
	{
		using TempFolder folder = new();
		String databasePath = IndexWithLsa(folder);

		using Microsoft.Data.Sqlite.SqliteConnection connection = new($"Data Source={databasePath}");
		connection.Open();

		using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
		command.CommandText =
			"SELECT provider_type, vector_dimension FROM embedding_model_registry WHERE model_version_id = 'lsa-v1';";

		using Microsoft.Data.Sqlite.SqliteDataReader reader = command.ExecuteReader();

		reader.Read().Should().BeTrue();
		reader.GetString(0).Should().Be("programmable-lsa");

		Int32 recorded = reader.GetInt32(1);
		new SqliteBlobVectorStoreReader().ReadVectorsByModelVersion(databasePath, "lsa-v1")
			.Should().OnlyContain(vector => vector.Vector.Count == recorded);
	}

	/// <summary>The baseline writes no artifact, because it has no fit to write.</summary>
	[Fact]
	public void A_Vectorizer_With_No_Fit_Stores_No_Artifact()
	{
		using TempFolder folder = new();
		WriteCorpus(folder);

		String databasePath = new FolderDatabaseBootstrapper()
			.EnsureInitialized(folder.Path, new PersistenceConfig())
			.DatabasePath;

		new FolderIndexingPipeline().Run(folder.Path, databasePath, BaseConfig with { ModelVersionId = "plain-v1" });

		new SqliteBlobVectorStoreReader().ReadFitArtifact(databasePath, "plain-v1").Should().BeNull();
	}

	private static readonly IndexingConfig BaseConfig = new()
	{
		ChunkSizeTokens = 8,
		ChunkOverlapTokens = 2,
		VectorDimension = 3,
		ModelVersionId = "lsa-v1",
	};

	private static String IndexWithLsa(TempFolder folder)
	{
		WriteCorpus(folder);

		String databasePath = new FolderDatabaseBootstrapper()
			.EnsureInitialized(folder.Path, new PersistenceConfig())
			.DatabasePath;

		new FolderIndexingPipeline(LsaEmbeddingVectorizer.CreateForFitting("lsa-v1", 3))
			.Run(folder.Path, databasePath, BaseConfig);

		return databasePath;
	}

	private static void WriteCorpus(TempFolder folder)
	{
		File.WriteAllText(
			folder.Combine("cars.md"),
			"the car has an engine and four wheels a car needs an engine to drive on the road",
			Encoding.UTF8);

		File.WriteAllText(
			folder.Combine("autos.md"),
			"the automobile has an engine and four wheels an automobile drives on the road",
			Encoding.UTF8);

		File.WriteAllText(
			folder.Combine("baking.md"),
			"bread flour yeast oven baking loaf the loaf of bread came from the oven",
			Encoding.UTF8);
	}
}
