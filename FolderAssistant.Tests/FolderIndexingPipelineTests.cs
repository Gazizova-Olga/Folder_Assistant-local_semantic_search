using System.Text;
using FluentAssertions;
using FolderAssistant.Indexing;
using FolderAssistant.Persistence;
using Microsoft.Data.Sqlite;

namespace FolderAssistant.Tests;

public sealed class FolderIndexingPipelineTests
{
	[Fact]
	public void The_Scanner_Includes_Only_Allowed_Text_Files()
	{
		using TempFolder folder = new();
		Directory.CreateDirectory(folder.Combine("src"));

		File.WriteAllText(folder.Combine("src", "a.cs"), "public class A {}", Encoding.UTF8);
		File.WriteAllText(folder.Combine("src", "note.md"), "hello world", Encoding.UTF8);
		File.WriteAllBytes(folder.Combine("src", "raw.bin"), [0, 1, 2, 3]);

		List<String> paths = new LocalTextFileScanner()
			.Scan(folder.Path, maxTextFileSizeBytes: 1024 * 1024)
			.Select(static file => file.RelativePath)
			.ToList();

		paths.Should().Contain("src/a.cs");
		paths.Should().Contain("src/note.md");
		paths.Should().NotContain("src/raw.bin");
	}

	[Fact]
	public void The_Scanner_Skips_Files_Over_The_Size_Limit()
	{
		using TempFolder folder = new();
		File.WriteAllText(folder.Combine("big.md"), new String('x', 4096), Encoding.UTF8);

		new LocalTextFileScanner().Scan(folder.Path, maxTextFileSizeBytes: 512).Should().BeEmpty();
	}

	[Fact]
	public void The_Scanner_Does_Not_Descend_Into_Generated_Directories()
	{
		using TempFolder folder = new();
		Directory.CreateDirectory(folder.Combine("obj"));
		Directory.CreateDirectory(folder.Combine(".folderassistant"));

		File.WriteAllText(folder.Combine("obj", "generated.cs"), "class G {}", Encoding.UTF8);
		File.WriteAllText(folder.Combine(".folderassistant", "notes.md"), "internal", Encoding.UTF8);
		File.WriteAllText(folder.Combine("real.md"), "content", Encoding.UTF8);

		new LocalTextFileScanner()
			.Scan(folder.Path, maxTextFileSizeBytes: 1024 * 1024)
			.Select(static file => file.RelativePath)
			.Should().BeEquivalentTo(["real.md"]);
	}

	[Fact]
	public void Chunks_Overlap_By_The_Configured_Number_Of_Tokens()
	{
		IReadOnlyList<String> tokens = ["a", "b", "c", "d", "e", "f"];

		IReadOnlyList<TextChunk> chunks = new TextChunker()
			.Chunk("file-1", tokens, chunkSizeTokens: 4, chunkOverlapTokens: 1);

		chunks[0].Content.Should().Be("a b c d");
		chunks[1].TokenStart.Should().Be(3, "a step of size minus overlap re-reads the last token");
		chunks[1].Content.Should().Be("d e f");
	}

	[Fact]
	public void The_Same_Text_Always_Embeds_To_The_Same_Vector()
	{
		ProgrammableEmbeddingVectorizer vectorizer = new();

		EmbeddingResult first = vectorizer.Vectorize("alpha beta", "m1", 32);
		EmbeddingResult second = vectorizer.Vectorize("alpha beta", "m1", 32);

		second.Vector.Should().Equal(first.Vector);
	}

	[Fact]
	public void The_Pipeline_Stores_Files_Chunks_And_Vectors()
	{
		using TempFolder folder = new();

		File.WriteAllText(
			folder.Combine("alpha.txt"),
			"alpha beta gamma delta epsilon zeta eta theta iota kappa",
			Encoding.UTF8);

		File.WriteAllText(
			folder.Combine("beta.md"),
			"one two three four five six seven eight nine ten",
			Encoding.UTF8);

		DatabaseBootstrapResult database = new FolderDatabaseBootstrapper()
			.EnsureInitialized(folder.Path, new PersistenceConfig());

		IndexingResult indexed = new FolderIndexingPipeline().Run(
			folder.Path,
			database.DatabasePath,
			new IndexingConfig
			{
				ChunkSizeTokens = 4,
				ChunkOverlapTokens = 1,
				VectorDimension = 32,
				ModelVersionId = "programmable-v1",
			});

		indexed.FilesIndexed.Should().BeGreaterThanOrEqualTo(2);
		indexed.ChunksIndexed.Should().BeGreaterThan(0);
		indexed.VectorsIndexed.Should().Be(indexed.ChunksIndexed);

		using SqliteConnection connection = new($"Data Source={database.DatabasePath}");
		connection.Open();

		Count(connection, "SELECT COUNT(*) FROM file_manifest;").Should().BeGreaterThanOrEqualTo(2);
		Count(connection, "SELECT COUNT(*) FROM chunk_manifest;").Should().Be(indexed.ChunksIndexed);
		Count(connection, "SELECT COUNT(*) FROM chunk_vector;").Should().Be(indexed.VectorsIndexed);
		Count(connection, "SELECT COUNT(*) FROM embedding_model_registry WHERE is_active_for_write = 1;")
			.Should().Be(1);
	}

	[Fact]
	public void Indexing_The_Same_Folder_Twice_Does_Not_Duplicate_Rows()
	{
		using TempFolder folder = new();
		File.WriteAllText(folder.Combine("alpha.txt"), "alpha beta gamma delta", Encoding.UTF8);

		DatabaseBootstrapResult database = new FolderDatabaseBootstrapper()
			.EnsureInitialized(folder.Path, new PersistenceConfig());

		IndexingConfig config = new() { ChunkSizeTokens = 4, ChunkOverlapTokens = 1, VectorDimension = 32 };

		FolderIndexingPipeline pipeline = new();
		pipeline.Run(folder.Path, database.DatabasePath, config);
		IndexingResult second = pipeline.Run(folder.Path, database.DatabasePath, config);

		using SqliteConnection connection = new($"Data Source={database.DatabasePath}");
		connection.Open();

		Count(connection, "SELECT COUNT(*) FROM file_manifest;").Should().Be(1);
		Count(connection, "SELECT COUNT(*) FROM chunk_manifest;").Should().Be(second.ChunksIndexed);
		Count(connection, "SELECT COUNT(*) FROM chunk_vector;").Should().Be(second.VectorsIndexed);
	}

	private static Int64 Count(SqliteConnection connection, String sql)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = sql;

		return (Int64)(command.ExecuteScalar() ?? 0L);
	}
}
