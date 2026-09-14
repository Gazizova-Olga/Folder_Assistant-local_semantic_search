using System.Text;
using FluentAssertions;
using FolderAssistant.Embedding;
using FolderAssistant.Embedding.Lsa;
using FolderAssistant.Indexing;
using FolderAssistant.Indexing.Scanning;
using FolderAssistant.Persistence;
using Microsoft.Data.Sqlite;

namespace FolderAssistant.Tests;

/// <summary>
/// Indexing used to re-embed every file on every run. The file hash was being written and never read
/// back; these cover reading it, and what has to be true besides the hash.
///
/// <para>
/// A removal is recorded and queued by the pass and delivered by the dispatcher, so the two tests
/// about deletion drain the outbox before they look — the way the running application would.
/// </para>
/// </summary>
public sealed class ChangeDetectionTests
{
	[Fact]
	public void An_Unchanged_File_Is_Not_Embedded_Again()
	{
		using TempFolder folder = new();
		WriteFile(folder, "alpha.md", "alpha beta gamma delta");

		String databasePath = Bootstrap(folder);
		IndexingResult first = Index(folder, databasePath);
		IndexingResult second = Index(folder, databasePath);

		first.FilesIndexed.Should().Be(1);
		second.FilesIndexed.Should().Be(0);
		second.FilesUnchanged.Should().Be(1);
		second.VectorsIndexed.Should().Be(0);
	}

	[Fact]
	public void A_File_Whose_Content_Changed_Is_Embedded_Again()
	{
		using TempFolder folder = new();
		WriteFile(folder, "alpha.md", "alpha beta gamma delta");

		String databasePath = Bootstrap(folder);
		Index(folder, databasePath);

		WriteFile(folder, "alpha.md", "completely different words entirely");
		IndexingResult second = Index(folder, databasePath);

		second.FilesIndexed.Should().Be(1);
		second.FilesUnchanged.Should().Be(0);
	}

	[Fact]
	public void A_New_File_Is_Embedded_While_The_Others_Are_Skipped()
	{
		using TempFolder folder = new();
		WriteFile(folder, "alpha.md", "alpha beta gamma delta");

		String databasePath = Bootstrap(folder);
		Index(folder, databasePath);

		WriteFile(folder, "beta.md", "epsilon zeta eta theta");
		IndexingResult second = Index(folder, databasePath);

		second.FilesIndexed.Should().Be(1);
		second.FilesUnchanged.Should().Be(1);
	}

	/// <summary>
	/// A manifest row with no file behind it is removed, and its chunks and vectors go with it. This
	/// is the orphan cleanup the persistence spec carried as open work.
	/// </summary>
	[Fact]
	public void A_Deleted_File_Is_Removed_And_Takes_Its_Chunks_And_Vectors_With_It()
	{
		using TempFolder folder = new();
		WriteFile(folder, "alpha.md", "alpha beta gamma delta");
		WriteFile(folder, "beta.md", "epsilon zeta eta theta");

		String databasePath = Bootstrap(folder);
		Index(folder, databasePath);

		File.Delete(folder.Combine("beta.md"));
		IndexingResult second = Index(folder, databasePath);

		// The pass records the removal and queues it; the delivery is what clears the vectors, then
		// the chunks, then the row — the order a native vector store needs.
		second.ChangesRecorded.Should().Be(1);
		OutboxDrain.Deliver(folder.Path, databasePath, ConfigFor("model-a")).Should().BeGreaterThan(0);

		using SqliteConnection connection = Connect(databasePath);
		Count(connection, "SELECT COUNT(*) FROM file_manifest;").Should().Be(1);

		// The cascade is the point: chunk and vector rows for the removed file must not survive it.
		Count(connection, $"SELECT COUNT(*) FROM chunk_manifest WHERE file_id = '{FileIdentity.For("beta.md")}';")
			.Should().Be(0);
		Count(connection, "SELECT COUNT(*) FROM chunk_manifest;")
			.Should().BeGreaterThan(0)
			.And.Be(Count(connection, "SELECT COUNT(*) FROM chunk_vector;"));
	}

	[Fact]
	public void Emptying_The_Folder_Empties_The_Index()
	{
		using TempFolder folder = new();
		WriteFile(folder, "alpha.md", "alpha beta gamma delta");

		String databasePath = Bootstrap(folder);
		Index(folder, databasePath);

		File.Delete(folder.Combine("alpha.md"));
		Index(folder, databasePath).ChangesRecorded.Should().Be(1);
		OutboxDrain.Deliver(folder.Path, databasePath, ConfigFor("model-a"));

		using SqliteConnection connection = Connect(databasePath);
		Count(connection, "SELECT COUNT(*) FROM file_manifest;").Should().Be(0);
		Count(connection, "SELECT COUNT(*) FROM chunk_manifest;").Should().Be(0);
		Count(connection, "SELECT COUNT(*) FROM chunk_vector;").Should().Be(0);
	}

	/// <summary>
	/// The property content equality alone would get wrong. After switching embedding implementation
	/// every file is unchanged, yet none of them has a vector in the new model's space — so skipping
	/// on the hash alone would leave the new model with a silently empty index.
	/// </summary>
	[Fact]
	public void Switching_Model_Re_Embeds_Every_File_Even_Though_Nothing_Changed()
	{
		using TempFolder folder = new();
		WriteFile(folder, "alpha.md", "alpha beta gamma delta");
		WriteFile(folder, "beta.md", "epsilon zeta eta theta");

		String databasePath = Bootstrap(folder);
		Index(folder, databasePath, "model-a");

		IndexingResult switched = Index(folder, databasePath, "model-b");

		switched.FilesIndexed.Should().Be(2, "no file has a vector in the new model's space");
		switched.FilesUnchanged.Should().Be(0);

		using SqliteConnection connection = Connect(databasePath);
		Count(connection, "SELECT COUNT(*) FROM chunk_vector WHERE model_version_id = 'model-a';")
			.Should().BeGreaterThan(0, "the previous model's vectors are left alone");
		Count(connection, "SELECT COUNT(*) FROM chunk_vector WHERE model_version_id = 'model-b';")
			.Should().BeGreaterThan(0);
	}

	/// <summary>
	/// Chunking still runs for every file; only embedding is skipped. A corpus-fitted vectorizer needs
	/// the whole chunk set regardless, and the scanner has already paid to read the content.
	/// </summary>
	[Fact]
	public void Chunk_Rows_Survive_A_Pass_That_Embeds_Nothing()
	{
		using TempFolder folder = new();
		WriteFile(folder, "alpha.md", "alpha beta gamma delta epsilon zeta");

		String databasePath = Bootstrap(folder);
		IndexingResult first = Index(folder, databasePath);
		Index(folder, databasePath);

		using SqliteConnection connection = Connect(databasePath);
		Count(connection, "SELECT COUNT(*) FROM chunk_manifest;").Should().Be(first.ChunksIndexed);
	}

	/// <summary>
	/// An existing fit is reused rather than recomputed. Refitting changes the projection and would
	/// invalidate every vector already stored under that model version.
	/// </summary>
	[Fact]
	public void An_Existing_Fit_Is_Reused_Rather_Than_Recomputed()
	{
		using TempFolder folder = new();
		WriteFile(folder, "cars.md", "the car has an engine and four wheels and drives on the road");
		WriteFile(folder, "autos.md", "an automobile has an engine and four wheels and drives on the road");

		String databasePath = Bootstrap(folder);

		new FolderIndexingPipeline(LsaEmbeddingVectorizer.CreateForFitting("lsa-v1", 2))
			.Run(folder.Path, databasePath, ConfigFor("lsa-v1"));

		SqliteBlobVectorStoreReader reader = new();
		String firstArtifact = reader.ReadFitArtifact(databasePath, "lsa-v1")!;

		// A new file changes the corpus. A refit would produce a different artifact; reuse must not.
		WriteFile(folder, "baking.md", "bread flour yeast oven baking loaf from the oven");

		new FolderIndexingPipeline(LsaEmbeddingVectorizer.CreateForFitting("lsa-v1", 2))
			.Run(folder.Path, databasePath, ConfigFor("lsa-v1"));

		reader.ReadFitArtifact(databasePath, "lsa-v1").Should().Be(firstArtifact);
	}

	private static readonly IndexingConfig BaseConfig = new()
	{
		ChunkSizeTokens = 4,
		ChunkOverlapTokens = 1,
		VectorDimension = 16,
		ModelVersionId = "model-a",
	};

	private static IndexingConfig ConfigFor(String modelVersionId)
		=> BaseConfig with { ModelVersionId = modelVersionId };

	private static IndexingResult Index(TempFolder folder, String databasePath, String modelVersionId = "model-a")
		=> new FolderIndexingPipeline().Run(folder.Path, databasePath, ConfigFor(modelVersionId));

	private static String Bootstrap(TempFolder folder)
		=> new FolderDatabaseBootstrapper()
			.EnsureInitialized(folder.Path, new PersistenceConfig())
			.DatabasePath;

	private static void WriteFile(TempFolder folder, String name, String content)
		=> File.WriteAllText(folder.Combine(name), content, Encoding.UTF8);

	private static SqliteConnection Connect(String databasePath)
	{
		SqliteConnection connection = new($"Data Source={databasePath}");
		connection.Open();

		return connection;
	}

	private static Int64 Count(SqliteConnection connection, String sql)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = sql;

		return (Int64)(command.ExecuteScalar() ?? 0L);
	}
}
