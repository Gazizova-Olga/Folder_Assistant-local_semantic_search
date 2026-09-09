using System.Text;
using FluentAssertions;
using FolderAssistant.Embedding;
using FolderAssistant.Indexing;
using FolderAssistant.Persistence;
using FolderAssistant.Retrieval;

namespace FolderAssistant.Tests;

/// <summary>
/// The reader against a real database. The ranking tests use a fake store; these cover the part a
/// fake cannot — that the join actually resolves a vector back to the file and window it came from,
/// and that model scoping is enforced in SQL rather than only in the caller.
/// </summary>
public sealed class VectorStoreReaderTests
{
	[Fact]
	public void Scoring_Reads_Return_Vectors_And_Nothing_Else()
	{
		using TempFolder folder = new();
		String databasePath = IndexOneFile(folder, "alpha beta gamma delta epsilon zeta");

		IReadOnlyList<StoredVector> stored =
			new SqliteBlobVectorStoreReader().ReadVectorsByModelVersion(databasePath, "model-a");

		stored.Should().NotBeEmpty();
		stored.Should().OnlyContain(vector => vector.Vector.Count == 16);
		stored.Should().OnlyContain(vector => !String.IsNullOrEmpty(vector.ChunkId));
	}

	/// <summary>
	/// The other half of the split. A hit has to be locatable — a chunk stores no text of its own,
	/// so retrieval carries the file and the token window that let the passage be rebuilt.
	/// </summary>
	[Fact]
	public void A_Ranked_Chunk_Resolves_Back_To_Its_File_And_Token_Window()
	{
		using TempFolder folder = new();
		String databasePath = IndexOneFile(folder, "alpha beta gamma delta epsilon zeta");

		SqliteBlobVectorStoreReader reader = new();
		IReadOnlyList<StoredVector> stored = reader.ReadVectorsByModelVersion(databasePath, "model-a");

		IReadOnlyDictionary<String, ChunkLocation> locations =
			reader.ReadChunkLocations(databasePath, [.. stored.Select(static v => v.ChunkId)]);

		locations.Should().HaveCount(stored.Count);
		locations.Values.Should().OnlyContain(location => location.FilePath == "notes.md");
		locations.Values.Select(static l => l.ChunkIndex).Should().BeEquivalentTo(Enumerable.Range(0, stored.Count));
		locations.Values.Should().OnlyContain(location => location.TokenEnd > location.TokenStart);
	}

	/// <summary>Asking for nothing does no work, rather than building an empty IN clause.</summary>
	[Fact]
	public void Resolving_No_Chunks_Returns_Nothing()
	{
		using TempFolder folder = new();
		String databasePath = IndexOneFile(folder, "alpha beta gamma");

		new SqliteBlobVectorStoreReader().ReadChunkLocations(databasePath, []).Should().BeEmpty();
	}

	[Fact]
	public void Reading_A_Model_Version_That_Was_Never_Indexed_Returns_Nothing()
	{
		using TempFolder folder = new();
		String databasePath = IndexOneFile(folder, "alpha beta gamma");

		new SqliteBlobVectorStoreReader().ReadVectorsByModelVersion(databasePath, "never-indexed").Should().BeEmpty();
	}

	[Fact]
	public void Two_Model_Versions_Are_Read_Apart()
	{
		using TempFolder folder = new();
		String databasePath = IndexOneFile(folder, "alpha beta gamma delta");

		new FolderIndexingPipeline().Run(folder.Path, databasePath, ConfigFor("model-b"));

		SqliteBlobVectorStoreReader reader = new();
		IReadOnlyList<StoredVector> first = reader.ReadVectorsByModelVersion(databasePath, "model-a");
		IReadOnlyList<StoredVector> second = reader.ReadVectorsByModelVersion(databasePath, "model-b");

		first.Should().NotBeEmpty();
		second.Should().NotBeEmpty();
		first.Select(static v => v.ChunkId).Should().BeEquivalentTo(second.Select(static v => v.ChunkId));
	}

	/// <summary>Nothing writes a fit artifact yet, so the absent case is the one that matters.</summary>
	[Fact]
	public void A_Model_Version_With_No_Fit_Artifact_Reads_Back_Null()
	{
		using TempFolder folder = new();
		String databasePath = IndexOneFile(folder, "alpha beta");

		new SqliteBlobVectorStoreReader().ReadFitArtifact(databasePath, "model-a").Should().BeNull();
	}

	/// <summary>End to end: index a folder, then find its content through the real reader.</summary>
	[Fact]
	public void A_Query_Retrieves_Content_That_Was_Just_Indexed()
	{
		using TempFolder folder = new();
		String databasePath = IndexOneFile(folder, "alpha beta gamma delta epsilon zeta eta theta");

		IReadOnlyList<RetrievalHit> hits = new CosineRetrievalQuery(
				new ProgrammableEmbeddingVectorizer("model-a", 16))
			.Search(databasePath, "alpha beta gamma delta", new RetrievalOptions(TopK: 3));

		hits.Should().NotBeEmpty();
		hits[0].FilePath.Should().Be("notes.md");
		hits[0].Score.Should().BeGreaterThan(0.0);
		hits.Select(static hit => hit.Score).Should().BeInDescendingOrder();
	}

	private static String IndexOneFile(TempFolder folder, String content)
	{
		File.WriteAllText(folder.Combine("notes.md"), content, Encoding.UTF8);

		String databasePath = new FolderDatabaseBootstrapper()
			.EnsureInitialized(folder.Path, new PersistenceConfig())
			.DatabasePath;

		new FolderIndexingPipeline().Run(folder.Path, databasePath, ConfigFor("model-a"));

		return databasePath;
	}

	private static IndexingConfig ConfigFor(String modelVersionId)
		=> new()
		{
			ChunkSizeTokens = 4,
			ChunkOverlapTokens = 1,
			VectorDimension = 16,
			ModelVersionId = modelVersionId,
		};
}
