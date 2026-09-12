using System.Text;
using FluentAssertions;
using FolderAssistant.Embedding;
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
			.Should().BeEquivalentTo("real.md");
	}

	[Fact]
	public void Chunks_Overlap_By_The_Configured_Number_Of_Tokens()
	{
		IReadOnlyList<String> tokens = ["a", "b", "c", "d", "e", "f"];

		IReadOnlyList<TextChunk> chunks = new TextChunker()
			.Chunk("file-1", TokenizedText.FromTokens(tokens), chunkSizeTokens: 4, chunkOverlapTokens: 1);

		chunks[0].Content.Should().Be("a b c d");
		chunks[1].TokenStart.Should().Be(3, "a step of size minus overlap re-reads the last token");
		chunks[1].Content.Should().Be("d e f");
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

	/// <summary>
	/// Only one model version may be active for write. Indexing the same folder with a second model is
	/// exactly the state that arises while comparing embedding implementations against one corpus, and
	/// the registry insert activates unconditionally — so something has to demote the previous row.
	/// </summary>
	[Fact]
	public void Indexing_With_A_Second_Model_Leaves_Exactly_One_Active_For_Write()
	{
		using TempFolder folder = new();
		File.WriteAllText(folder.Combine("alpha.txt"), "alpha beta gamma delta", Encoding.UTF8);

		DatabaseBootstrapResult database = new FolderDatabaseBootstrapper()
			.EnsureInitialized(folder.Path, new PersistenceConfig());

		IndexingConfig first = new()
		{
			ChunkSizeTokens = 4,
			ChunkOverlapTokens = 1,
			VectorDimension = 32,
			ModelVersionId = "model-a",
		};
		new FolderIndexingPipeline().Run(folder.Path, database.DatabasePath, first);

		IndexingConfig second = first with { ModelVersionId = "model-b" };
		new FolderIndexingPipeline().Run(folder.Path, database.DatabasePath, second);

		using SqliteConnection connection = new($"Data Source={database.DatabasePath}");
		connection.Open();

		Count(connection, "SELECT COUNT(*) FROM embedding_model_registry;").Should().Be(2);
		Count(connection, "SELECT COUNT(*) FROM embedding_model_registry WHERE is_active_for_write = 1;")
			.Should().Be(1);
		Count(connection, "SELECT COUNT(*) FROM embedding_model_registry WHERE is_active_for_write = 1 AND model_version_id = 'model-b';")
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
		IndexingResult first = pipeline.Run(folder.Path, database.DatabasePath, config);
		IndexingResult second = pipeline.Run(folder.Path, database.DatabasePath, config);

		using SqliteConnection connection = new($"Data Source={database.DatabasePath}");
		connection.Open();

		// The second pass embeds nothing, because nothing changed — but the rows the first pass wrote
		// are still there. Counting against the second pass's own totals would now assert the wrong
		// thing entirely, which is what it did before skipping existed.
		second.FilesIndexed.Should().Be(0);
		second.FilesUnchanged.Should().Be(1);

		Count(connection, "SELECT COUNT(*) FROM file_manifest;").Should().Be(1);
		Count(connection, "SELECT COUNT(*) FROM chunk_manifest;").Should().Be(first.ChunksIndexed);
		Count(connection, "SELECT COUNT(*) FROM chunk_vector;").Should().Be(first.VectorsIndexed);
	}

	/// <summary>
	/// A whole-folder pass ends by folding its write-ahead log back into the database. A reader stays open
	/// throughout, the way retrieval does while indexing runs: with no connection left, SQLite would fold and
	/// remove the log on its own when the last one closed, and the test could not tell a checkpoint from that.
	/// </summary>
	[Fact]
	public void A_Whole_Folder_Pass_Leaves_No_Write_Ahead_Log_Behind()
	{
		using TempFolder folder = new();

		for (Int32 i = 0; i < 50; i++)
		{
			File.WriteAllText(folder.Combine($"doc{i:D2}.md"), $"document {i} " + String.Join(' ', Enumerable.Range(0, 200)), Encoding.UTF8);
		}

		DatabaseBootstrapResult database = new FolderDatabaseBootstrapper()
			.EnsureInitialized(folder.Path, new PersistenceConfig());

		using SqliteConnection reader = FolderDatabaseConnection.OpenRead(database.DatabasePath);
		Count(reader, "SELECT COUNT(*) FROM file_manifest;").Should().Be(0);

		new FolderIndexingPipeline().Run(
			folder.Path,
			database.DatabasePath,
			new IndexingConfig { ChunkSizeTokens = 16, ChunkOverlapTokens = 4, VectorDimension = 32 });

		new FileInfo(database.DatabasePath + "-wal").Length.Should().Be(0);
		Count(reader, "SELECT COUNT(*) FROM file_manifest;").Should().Be(50, "the checkpoint reclaims disk and changes nothing stored");
	}

	// ── the embed window ──────────────────────────────────────────────────────

	/// <summary>
	/// The window exists to bound memory, and it is allowed to cut anywhere precisely because cutting
	/// changes nothing that is stored: every chunk is embedded independently of the others sharing its
	/// call. If that were ever untrue, the window would silently become a correctness knob — the same
	/// folder would index to different vectors depending on a number chosen for memory reasons, and
	/// nothing at query time could tell.
	/// </summary>
	[Fact]
	public void The_Embed_Window_Does_Not_Change_A_Single_Stored_Vector()
	{
		Dictionary<String, Byte[]> oneAtATime = VectorsIndexedWithWindow(1);
		Dictionary<String, Byte[]> wholeCorpusAtOnce = VectorsIndexedWithWindow(1000);
		Dictionary<String, Byte[]> defaultWindow = VectorsIndexedWithWindow(new IndexingConfig().EmbeddingBatchSizeChunks);

		oneAtATime.Should().NotBeEmpty("the corpus has to produce vectors for the comparison to mean anything");
		oneAtATime.Keys.Should().BeEquivalentTo(wholeCorpusAtOnce.Keys).And.BeEquivalentTo(defaultWindow.Keys);

		foreach ((String chunkId, Byte[] vector) in oneAtATime)
		{
			wholeCorpusAtOnce[chunkId].Should().Equal(vector, "the window may not move a stored vector");
			defaultWindow[chunkId].Should().Equal(vector, "and the shipped default may not either");
		}
	}

	/// <summary>
	/// Chunks are gathered across files, not flushed at each file boundary. Per file, a folder of small
	/// files costs one round trip each — and for an embedder reached over a socket the call count is
	/// very nearly the whole cost of a first index.
	/// </summary>
	[Fact]
	public void Chunks_Are_Gathered_Across_Files_Rather_Than_Flushed_Per_File()
	{
		using TempFolder folder = new();

		for (Int32 i = 0; i < 12; i++)
		{
			File.WriteAllText(folder.Combine($"file{i:D2}.txt"), $"document number {i} with a little text", Encoding.UTF8);
		}

		RecordingVectorizer vectorizer = new(new ProgrammableEmbeddingVectorizer("programmable-v1", 32));

		Index(folder, vectorizer, windowSize: 64);

		vectorizer.BatchSizes.Should().ContainSingle(
			"twelve one-chunk files fit inside one window, so they should cost one call rather than twelve");
		vectorizer.BatchSizes[0].Should().Be(12);
	}

	/// <summary>
	/// A window of one is the per-file behaviour this replaced, and it stays reachable: an embedder that
	/// cannot take a large array, or a machine where holding one window of text is already too much,
	/// needs a way back.
	/// </summary>
	[Fact]
	public void A_Window_Of_One_Embeds_Each_Chunk_On_Its_Own()
	{
		using TempFolder folder = new();

		for (Int32 i = 0; i < 5; i++)
		{
			File.WriteAllText(folder.Combine($"file{i}.txt"), $"document number {i}", Encoding.UTF8);
		}

		RecordingVectorizer vectorizer = new(new ProgrammableEmbeddingVectorizer("programmable-v1", 32));

		Index(folder, vectorizer, windowSize: 1);

		vectorizer.BatchSizes.Should().HaveCount(5).And.OnlyContain(size => size == 1);
	}

	/// <summary>
	/// A window below one is meaningless, and the two nonsense values fail differently without a clamp:
	/// zero already behaves as one, because the flush test fires as soon as anything is pending, while a
	/// negative throws out of the buffer's capacity argument. Both are normalised to the per-chunk
	/// behaviour rather than one of them taking the whole pass down.
	/// </summary>
	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	public void A_Window_Below_One_Falls_Back_To_A_Chunk_At_A_Time(Int32 windowSize)
	{
		using TempFolder folder = new();

		for (Int32 i = 0; i < 4; i++)
		{
			File.WriteAllText(folder.Combine($"file{i}.txt"), $"document number {i}", Encoding.UTF8);
		}

		RecordingVectorizer vectorizer = new(new ProgrammableEmbeddingVectorizer("programmable-v1", 32));

		Index(folder, vectorizer, windowSize);

		vectorizer.BatchSizes.Should().HaveCount(4).And.OnlyContain(size => size == 1);
	}

	private static Dictionary<String, Byte[]> VectorsIndexedWithWindow(Int32 windowSize)
	{
		using TempFolder folder = new();

		// Several files, several chunks each, so that a window can fall inside a file as well as between
		// two — the boundary the per-file version could never produce.
		for (Int32 i = 0; i < 7; i++)
		{
			File.WriteAllText(
				folder.Combine($"file{i}.txt"),
				String.Join(' ', Enumerable.Range(0, 40).Select(word => $"file{i}word{word}")),
				Encoding.UTF8);
		}

		String databasePath = Index(
			folder,
			new ProgrammableEmbeddingVectorizer("programmable-v1", 32),
			windowSize,
			chunkSizeTokens: 8,
			chunkOverlapTokens: 2);

		Dictionary<String, Byte[]> vectors = new(StringComparer.Ordinal);

		using SqliteConnection connection = new($"Data Source={databasePath}");
		connection.Open();

		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT chunk_id, vector FROM chunk_vector ORDER BY chunk_id;";

		using SqliteDataReader reader = command.ExecuteReader();
		while (reader.Read())
		{
			vectors[reader.GetString(0)] = (Byte[])reader["vector"];
		}

		return vectors;
	}

	private static String Index(
		TempFolder folder,
		IVectorizer vectorizer,
		Int32 windowSize,
		Int32 chunkSizeTokens = 256,
		Int32 chunkOverlapTokens = 32)
	{
		DatabaseBootstrapResult database = new FolderDatabaseBootstrapper()
			.EnsureInitialized(folder.Path, new PersistenceConfig());

		new FolderIndexingPipeline(vectorizer).Run(
			folder.Path,
			database.DatabasePath,
			new IndexingConfig
			{
				ChunkSizeTokens = chunkSizeTokens,
				ChunkOverlapTokens = chunkOverlapTokens,
				VectorDimension = 32,
				ModelVersionId = "programmable-v1",
				EmbeddingBatchSizeChunks = windowSize,
			});

		return database.DatabasePath;
	}

	/// <summary>Passes everything through, and records how many texts each call was handed.</summary>
	private sealed class RecordingVectorizer(IVectorizer inner) : IVectorizer
	{
		public List<Int32> BatchSizes { get; } = [];

		public ModelDescriptor Descriptor => inner.Descriptor;

		public ValueTask<IReadOnlyList<EmbeddingResult>> VectorizeAsync(
			IReadOnlyList<String> texts,
			EmbeddingKind kind,
			CancellationToken cancellationToken = default)
		{
			this.BatchSizes.Add(texts.Count);

			return inner.VectorizeAsync(texts, kind, cancellationToken);
		}
	}

	private static Int64 Count(SqliteConnection connection, String sql)
	{
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = sql;

		return (Int64)(command.ExecuteScalar() ?? 0L);
	}
}
