using FluentAssertions;
using FolderAssistant.Embedding;
using FolderAssistant.Indexing;
using FolderAssistant.Persistence;
using FolderAssistant.Retrieval;
using Microsoft.Data.Sqlite;

namespace FolderAssistant.Tests;

/// <summary>
/// The sqlite-vec backend as an alternative vector store.
///
/// <para>
/// These are correctness tests rather than a benchmark. The point is that the two backends are
/// genuinely interchangeable behind the contracts — which is what makes comparing them meaningful at
/// all. A backend that ranked differently, or that lost track of what it had embedded, would produce
/// numbers describing something other than the same job done two ways.
/// </para>
/// </summary>
public sealed class SqliteVecBackendTests
{
	private const String ModelVersionId = "vec-test-v1";
	private const Int32 Dimension = 64;

	/// <summary>
	/// Not an assertion about the product so much as about the machine this suite is running on. The
	/// package ships no win-arm64 or musl binary, so a skip here is the honest outcome elsewhere —
	/// but on a platform that does have one, silently skipping the rest would hide a real regression.
	/// </summary>
	[Fact]
	public void The_Native_Extension_Reports_Its_Availability_Honestly()
	{
		if (!SqliteVecExtension.IsAvailable)
		{
			return;
		}

		using TempFolder folder = new();
		String databasePath = Bootstrap(folder);

		FluentActions.Invoking(() =>
		{
			using SqliteConnection connection =
				FolderDatabaseConnection.OpenRead(databasePath, withVectorExtension: true);
		}).Should().NotThrow("IsAvailable said the binary is there");
	}

	[Fact]
	public void Indexing_Through_The_Vec_Backend_Makes_Content_Retrievable()
	{
		if (!SqliteVecExtension.IsAvailable)
		{
			return;
		}

		using TempFolder folder = new();
		File.WriteAllText(folder.Combine("cake.md"), "chocolate cake recipe with butter and sugar");
		File.WriteAllText(folder.Combine("engine.md"), "diesel engine maintenance torque and pistons");

		String databasePath = Bootstrap(folder);
		ProgrammableEmbeddingVectorizer vectorizer = new(ModelVersionId, Dimension);

		VecPipeline(vectorizer).Run(folder.Path, databasePath, Config());

		IReadOnlyList<RetrievalHit> hits = new SqliteVecRetrievalQuery(vectorizer)
			.Search(databasePath, "chocolate cake recipe with butter and sugar", new RetrievalOptions(TopK: 1));

		hits.Should().ContainSingle();
		hits[0].FilePath.Should().Be("cake.md");
	}

	/// <summary>
	/// The property the whole comparison rests on. Two backends that rank differently are not two
	/// implementations of one thing, and any measurement putting their timings side by side would be
	/// comparing different work.
	/// </summary>
	[Fact]
	public void The_Vec_Backend_Ranks_The_Same_Chunks_As_The_Brute_Force_Baseline()
	{
		if (!SqliteVecExtension.IsAvailable)
		{
			return;
		}

		using TempFolder blobFolder = new();
		using TempFolder vecFolder = new();

		foreach (TempFolder folder in new[] { blobFolder, vecFolder })
		{
			File.WriteAllText(folder.Combine("alpha.md"), "alpha beta gamma delta epsilon zeta");
			File.WriteAllText(folder.Combine("beta.md"), "storage retrieval embedding vector index");
			File.WriteAllText(folder.Combine("gamma.md"), "chocolate cake butter sugar flour eggs");
		}

		String blobDatabase = Bootstrap(blobFolder);
		String vecDatabase = Bootstrap(vecFolder);

		ProgrammableEmbeddingVectorizer blobVectorizer = new(ModelVersionId, Dimension);
		ProgrammableEmbeddingVectorizer vecVectorizer = new(ModelVersionId, Dimension);

		new FolderIndexingPipeline(blobVectorizer, new SqliteBlobVectorStoreWriter(), new SqliteBlobVectorStoreReader())
			.Run(blobFolder.Path, blobDatabase, Config());
		VecPipeline(vecVectorizer).Run(vecFolder.Path, vecDatabase, Config());

		const String Query = "storage retrieval embedding vector index";
		RetrievalOptions options = new(TopK: 3);

		IReadOnlyList<RetrievalHit> blobHits =
			new CosineRetrievalQuery(blobVectorizer).Search(blobDatabase, Query, options);
		IReadOnlyList<RetrievalHit> vecHits =
			new SqliteVecRetrievalQuery(vecVectorizer).Search(vecDatabase, Query, options);

		vecHits.Select(hit => hit.FilePath).Should().Equal(blobHits.Select(hit => hit.FilePath));

		// Same metric, so the scores must agree too — not merely the order. vec0 returns a cosine
		// distance which is converted back, and a conversion that were wrong would still order
		// correctly while reporting a score nothing could be compared against.
		for (Int32 i = 0; i < vecHits.Count; i++)
		{
			vecHits[i].Score.Should().BeApproximately(blobHits[i].Score, 1e-5);
		}
	}

	/// <summary>
	/// The defect this backend introduces if the "already embedded?" question is answered against
	/// <c>chunk_vector</c>: the vec backend never writes that table, so every file would look
	/// un-embedded and be re-embedded on every single run — silently, with the index still correct.
	/// </summary>
	[Fact]
	public void Reindexing_An_Unchanged_Folder_Through_The_Vec_Backend_Embeds_Nothing()
	{
		if (!SqliteVecExtension.IsAvailable)
		{
			return;
		}

		using TempFolder folder = new();
		File.WriteAllText(folder.Combine("a.md"), "alpha beta gamma delta");
		File.WriteAllText(folder.Combine("b.md"), "storage retrieval embedding");

		String databasePath = Bootstrap(folder);
		ProgrammableEmbeddingVectorizer vectorizer = new(ModelVersionId, Dimension);

		IndexingResult first = VecPipeline(vectorizer).Run(folder.Path, databasePath, Config());
		first.FilesIndexed.Should().Be(2);

		IndexingResult second = VecPipeline(vectorizer).Run(folder.Path, databasePath, Config());

		second.FilesIndexed.Should().Be(0, "nothing changed, so nothing needed re-embedding");
		second.FilesUnchanged.Should().Be(2);
	}

	[Fact]
	public void Deleting_A_File_Removes_Its_Vectors_From_The_Vec_Table()
	{
		if (!SqliteVecExtension.IsAvailable)
		{
			return;
		}

		using TempFolder folder = new();
		File.WriteAllText(folder.Combine("keep.md"), "alpha beta gamma delta");
		File.WriteAllText(folder.Combine("drop.md"), "chocolate cake butter sugar");

		String databasePath = Bootstrap(folder);
		ProgrammableEmbeddingVectorizer vectorizer = new(ModelVersionId, Dimension);

		VecPipeline(vectorizer).Run(folder.Path, databasePath, Config());
		CountVectors(databasePath).Should().Be(2);

		File.Delete(folder.Combine("drop.md"));
		VecPipeline(vectorizer).Run(folder.Path, databasePath, Config());

		// A virtual table cannot be a foreign-key target, so nothing cascades here. If the explicit
		// delete were missing the row would simply survive its file.
		CountVectors(databasePath).Should().Be(1);
	}

	[Fact]
	public void A_Model_Version_That_Was_Never_Indexed_Returns_No_Hits()
	{
		if (!SqliteVecExtension.IsAvailable)
		{
			return;
		}

		using TempFolder folder = new();
		File.WriteAllText(folder.Combine("a.md"), "alpha beta gamma delta");

		String databasePath = Bootstrap(folder);
		VecPipeline(new ProgrammableEmbeddingVectorizer(ModelVersionId, Dimension))
			.Run(folder.Path, databasePath, Config());

		// A different model version has no table at all, so there is nothing to fall back to — which
		// is the point. Scoring against another model's vectors would be a number with no meaning.
		IReadOnlyList<RetrievalHit> hits =
			new SqliteVecRetrievalQuery(new ProgrammableEmbeddingVectorizer("never-indexed", Dimension))
				.Search(databasePath, "alpha beta gamma delta", new RetrievalOptions(TopK: 5));

		hits.Should().BeEmpty();
	}

	/// <summary>
	/// vec0 backs each virtual table with internal shadow tables sharing its name prefix, so anything
	/// enumerating them by name alone picks up implementation detail with no <c>chunk_id</c> column.
	/// </summary>
	[Fact]
	public void Enumerating_Vec_Tables_Returns_The_Virtual_Tables_And_Not_Their_Shadow_Tables()
	{
		if (!SqliteVecExtension.IsAvailable)
		{
			return;
		}

		using TempFolder folder = new();
		File.WriteAllText(folder.Combine("a.md"), "alpha beta gamma delta");

		String databasePath = Bootstrap(folder);
		VecPipeline(new ProgrammableEmbeddingVectorizer(ModelVersionId, Dimension))
			.Run(folder.Path, databasePath, Config());

		using SqliteConnection connection =
			FolderDatabaseConnection.OpenRead(databasePath, withVectorExtension: true);

		SqliteVecTable.ExistingTables(connection)
			.Should().ContainSingle().Which.Should().Be(SqliteVecTable.NameFor(ModelVersionId));
	}

	private static FolderIndexingPipeline VecPipeline(IVectorizer vectorizer)
		=> new(vectorizer, new SqliteVecVectorStoreWriter(), new SqliteVecVectorStoreReader());

	private static IndexingConfig Config()
		=> new() { ModelVersionId = ModelVersionId, VectorDimension = Dimension };

	private static String Bootstrap(TempFolder folder)
		=> new FolderDatabaseBootstrapper().EnsureInitialized(folder.Path, new PersistenceConfig()).DatabasePath;

	private static Int32 CountVectors(String databasePath)
	{
		using SqliteConnection connection =
			FolderDatabaseConnection.OpenRead(databasePath, withVectorExtension: true);

		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = $"SELECT COUNT(*) FROM {SqliteVecTable.NameFor(ModelVersionId)};";

		return Convert.ToInt32(command.ExecuteScalar());
	}
}
