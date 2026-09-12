using System.Text;
using FluentAssertions;
using FolderAssistant.Embedding;
using FolderAssistant.Embedding.Lsa;
using FolderAssistant.Indexing;
using FolderAssistant.Indexing.Outbox;
using FolderAssistant.Persistence;
using FolderAssistant.Retrieval;
using Microsoft.Data.Sqlite;

namespace FolderAssistant.Tests;

/// <summary>
/// The application's half of the seam the indexing library calls back through: one delivered file in,
/// searchable content out.
///
/// <para>
/// Most of these are about what it refuses. The path runs unattended, against a folder the system does
/// not own, and every refusal below is one that would otherwise be invisible — an index that never goes
/// quiet, vectors embedded in a space no query can reach, or chunks left behind for a file that is gone.
/// </para>
/// </summary>
public sealed class RagBridgeVectorizationServiceTests
{
	[Fact]
	public async Task A_Delivered_File_Becomes_Chunks_And_Vectors_Under_The_Delivered_Id()
	{
		using TempFolder folder = new();
		string path = folder.Combine("notes.md");
		await File.WriteAllTextAsync(path, "the harbour wall was rebuilt after the storm of that winter");

		using Bridge bridge = Bridge.For(folder);

		await bridge.DeliverAsync("doc-1", path);

		bridge.FileIds().Should().Equal(["doc-1"], "the delivered id is the identity, not something derived here");
		bridge.ChunkCountFor("doc-1").Should().BeGreaterThan(0);
		bridge.VectorCount().Should().Be(bridge.ChunkCountFor("doc-1"),
			"a chunk whose vector did not land can never be retrieved, and looks like a chunk nothing matches");
	}

	/// <summary>
	/// Delivery is at-least-once, and an edit re-delivers the same file. The second delivery must replace
	/// the first rather than accumulate beside it — a content edit yields a new content-addressed chunk id
	/// for the same slot, which the unique constraint would otherwise reject.
	/// </summary>
	[Fact]
	public async Task Re_Delivering_An_Edited_File_Replaces_Its_Chunks_Rather_Than_Adding_To_Them()
	{
		using TempFolder folder = new();
		string path = folder.Combine("notes.md");
		await File.WriteAllTextAsync(path, "the first version of this document, which said one thing");

		using Bridge bridge = Bridge.For(folder);
		await bridge.DeliverAsync("doc-1", path);
		int first = bridge.ChunkCountFor("doc-1");

		await File.WriteAllTextAsync(path, "the second version, wholly rewritten, saying something else");
		await bridge.DeliverAsync("doc-1", path);

		bridge.FileIds().Should().Equal("doc-1");
		bridge.ChunkCountFor("doc-1").Should().BeGreaterThan(0).And.Be(first);
		bridge.VectorCount().Should().Be(bridge.ChunkCountFor("doc-1"));
	}

	[Fact]
	public async Task A_Delete_Removes_The_File_Its_Chunks_And_Its_Vectors()
	{
		using TempFolder folder = new();
		string path = folder.Combine("notes.md");
		await File.WriteAllTextAsync(path, "something worth indexing and then removing again");

		using Bridge bridge = Bridge.For(folder);
		await bridge.DeliverAsync("doc-1", path);

		await bridge.Service.DeleteAsync("doc-1", CancellationToken.None);

		bridge.FileIds().Should().BeEmpty();
		bridge.ChunkCountFor("doc-1").Should().Be(0);
		bridge.VectorCount().Should().Be(0, "a vector outliving its chunk is a hit that cannot be resolved");
	}

	/// <summary>
	/// The same deletion, against the native backend, which is the only configuration that can observe
	/// the property.
	///
	/// <para>
	/// Deleting vectors explicitly looks redundant under the blob backend, because the chunk rows cascade
	/// and take their vectors with them — so a test on that backend passes whether or not the explicit
	/// delete is there, and mutating it away kills nothing. It is not redundant: a native store keeps
	/// vectors in a virtual table, and a virtual table cannot be the target of a foreign key. Here the
	/// cascade cannot fire, and a vector outliving its chunk is a hit that resolves to nothing.
	/// </para>
	/// </summary>
	[Fact]
	public async Task A_Delete_Removes_Vectors_The_Cascade_Cannot_Reach()
	{
		if (!SqliteVecExtension.IsAvailable)
		{
			return;
		}

		using TempFolder folder = new();
		string path = folder.Combine("notes.md");
		await File.WriteAllTextAsync(path, "content stored where a foreign key cannot reach it");

		using Bridge bridge = Bridge.For(folder, vectorStore: "vec");
		await bridge.DeliverAsync("doc-1", path);

		bridge.VectorCount("vec").Should().BeGreaterThan(0, "the delivery has to have stored something to remove");

		await bridge.Service.DeleteAsync("doc-1", CancellationToken.None);

		bridge.VectorCount("vec").Should().Be(0, "nothing cascades in a virtual table");
	}

	/// <summary>
	/// At-least-once delivery means a delete can arrive twice, or arrive for a file whose upsert was
	/// skipped. Neither is an error, and treating it as one would abandon the operation after its
	/// attempts ran out and mark a file failed for having nothing to remove.
	/// </summary>
	[Fact]
	public async Task Deleting_Something_That_Was_Never_Indexed_Is_Not_An_Error()
	{
		using TempFolder folder = new();
		using Bridge bridge = Bridge.For(folder);

		Func<Task> deleteTwice = async () =>
		{
			await bridge.Service.DeleteAsync("never-seen", CancellationToken.None);
			await bridge.Service.DeleteAsync("never-seen", CancellationToken.None);
		};

		await deleteTwice.Should().NotThrowAsync();
	}

	/// <summary>
	/// The database lives inside the folder being watched, so indexing it would make every write a change
	/// to that folder and the indexer would never go quiet — each pass triggering the next for as long as
	/// the process runs.
	/// </summary>
	[Fact]
	public async Task A_File_Inside_The_Metadata_Folder_Is_Refused()
	{
		using TempFolder folder = new();
		Directory.CreateDirectory(folder.Combine(".folderassistant"));
		string path = folder.Combine(".folderassistant", "stray-notes.md");
		await File.WriteAllTextAsync(path, "anything at all sitting in the metadata folder, which must never be indexed");

		using Bridge bridge = Bridge.For(folder);

		await bridge.DeliverAsync("doc-1", path);

		bridge.FileIds().Should().BeEmpty("indexing the index is how an indexer stops going quiet");
	}

	[Fact]
	public async Task A_File_Whose_Extension_This_System_Does_Not_Read_Is_Refused()
	{
		using TempFolder folder = new();
		string path = folder.Combine("photo.png");
		await File.WriteAllTextAsync(path, "not really a png, but the extension is what decides");

		using Bridge bridge = Bridge.For(folder);

		await bridge.DeliverAsync("doc-1", path);

		bridge.FileIds().Should().BeEmpty();
	}

	/// <summary>
	/// The extension question is asked in one place. A second list here would drift from the scanner's,
	/// and the drift would be silent: a file indexed by one path and ignored by the other is
	/// indistinguishable from a file that was never saved.
	/// </summary>
	[Fact]
	public void The_Extension_Rule_Is_The_Scanners_Own()
	{
		LocalTextFileScanner.IsIndexableExtension(".md").Should().BeTrue();
		LocalTextFileScanner.IsIndexableExtension(".png").Should().BeFalse();
		LocalTextFileScanner.IsIndexableExtension("").Should().BeFalse();
		LocalTextFileScanner.IsIndexableExtension(null).Should().BeFalse();
	}

	/// <summary>
	/// A corpus-fitted embedder cannot be fitted from one file, and embedding against no fit would store
	/// vectors in a space nothing can query — which no query could detect, because such a vector is not
	/// malformed, it simply means something else. Writing nothing leaves the file to the corpus pass.
	/// </summary>
	[Fact]
	public async Task A_Corpus_Fitted_Embedder_With_No_Stored_Fit_Writes_Nothing()
	{
		using TempFolder folder = new();
		string path = folder.Combine("notes.md");
		await File.WriteAllTextAsync(path, "a document delivered before any corpus pass has fitted the model");

		using Bridge bridge = Bridge.For(
			folder, LsaEmbeddingVectorizer.CreateForFitting("lsa-v1", 8));

		await bridge.DeliverAsync("doc-1", path);

		bridge.FileIds().Should().BeEmpty("vectors in an unfitted space rank as though they meant something else");
	}

	/// <summary>
	/// The dispatcher delivers several files at once and SQLite takes one writer, so every delivery has
	/// to land rather than one winning the lock and the rest being lost.
	///
	/// <para>
	/// <strong>This does not demonstrate the gate that serialises them.</strong> Widening the gate so the
	/// deliveries genuinely overlap leaves this test passing, because the busy timeout every connection
	/// sets absorbs the contention at this scale. What the gate buys is bounded queueing rather than
	/// timeout-and-retry, and nothing here separates the two — said plainly instead of left to look like
	/// coverage it is not.
	/// </para>
	/// </summary>
	[Fact]
	public async Task Concurrent_Deliveries_All_Land()
	{
		using TempFolder folder = new();
		using Bridge bridge = Bridge.For(folder);

		string[] paths = new string[8];

		for (int i = 0; i < paths.Length; i++)
		{
			paths[i] = folder.Combine($"note{i}.md");
			await File.WriteAllTextAsync(paths[i], $"document number {i}, with enough words to make a chunk of it");
		}

		await Task.WhenAll(Enumerable.Range(0, paths.Length)
			.Select(i => bridge.DeliverAsync($"doc-{i}", paths[i])));

		bridge.FileIds().Should().HaveCount(paths.Length);
		bridge.VectorCount().Should().BeGreaterThanOrEqualTo(paths.Length);
	}

	/// <summary>
	/// What the bridge writes has to be reachable by the thing it was written for. The chunk rows and the
	/// vectors could both be present and still be unqueryable — retrieval resolves a hit through the file
	/// row, so a delivery that wrote no file row would store content nothing could ever return.
	/// </summary>
	[Fact]
	public async Task A_Delivered_File_Is_Retrievable_Afterwards()
	{
		using TempFolder folder = new();
		string path = folder.Combine("harbour.md");
		await File.WriteAllTextAsync(path, "the harbour wall was rebuilt after the storm of that winter");

		using Bridge bridge = Bridge.For(folder);
		await bridge.DeliverAsync("doc-1", path);

		IReadOnlyList<RetrievalHit> hits = new CosineRetrievalQuery(bridge.Vectorizer)
			.Search(bridge.DatabasePath, "harbour wall rebuilt", new RetrievalOptions(TopK: 5));

		hits.Should().NotBeEmpty();
		hits[0].FilePath.Should().Contain("harbour.md");
	}

	/// <summary>Builds the bridge over a real database, and reads back what it wrote.</summary>
	private sealed class Bridge : IDisposable
	{
		private Bridge(string databasePath, IVectorizer vectorizer, RagBridgeVectorizationService service)
		{
			this.DatabasePath = databasePath;
			this.Vectorizer = vectorizer;
			this.Service = service;
		}

		public string DatabasePath { get; }

		public IVectorizer Vectorizer { get; }

		public RagBridgeVectorizationService Service { get; }

		public static Bridge For(TempFolder folder, IVectorizer? vectorizer = null, string vectorStore = "blob")
		{
			IndexingConfig config = new() { VectorDimension = 32, ModelVersionId = "programmable-v1" };
			IVectorizer resolved = vectorizer ?? new ProgrammableEmbeddingVectorizer(config.ModelVersionId, config.VectorDimension);

			bool vec = vectorStore == "vec";

			IVectorStoreWriter writer = vec ? new SqliteVecVectorStoreWriter() : new SqliteBlobVectorStoreWriter();
			IVectorStoreReader reader = vec ? new SqliteVecVectorStoreReader() : new SqliteBlobVectorStoreReader();

			string databasePath = new FolderDatabaseBootstrapper()
				.EnsureInitialized(folder.Path, new PersistenceConfig()).DatabasePath;

			RagBridgeVectorizationService service = new(
				folder.Path,
				databasePath,
				resolved,
				new FolderIndexRepository(writer),
				reader,
				config,
				".folderassistant");

			return new Bridge(databasePath, resolved, service);
		}

		public async Task DeliverAsync(string docId, string absolutePath, string? extension = null)
		{
			byte[] bytes = await File.ReadAllBytesAsync(absolutePath);
			using MemoryStream content = new(bytes);

			FileMetadata metadata = new(
				absolutePath,
				bytes.Length,
				new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc),
				extension ?? Path.GetExtension(absolutePath),
				Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));

			await this.Service.UpsertAsync(docId, content, metadata, CancellationToken.None);
		}

		public string[] FileIds()
		{
			List<string> ids = [];

			using SqliteConnection connection = Open(this.DatabasePath);
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "SELECT file_id FROM file_manifest ORDER BY file_id;";

			using SqliteDataReader reader = command.ExecuteReader();
			while (reader.Read())
			{
				ids.Add(reader.GetString(0));
			}

			return [.. ids];
		}

		public int ChunkCountFor(string fileId)
			=> (int)Scalar(this.DatabasePath, "SELECT COUNT(*) FROM chunk_manifest WHERE file_id = $id;", fileId);

		public int VectorCount(string vectorStore = "blob")
		{
			if (vectorStore != "vec")
			{
				return (int)Scalar(this.DatabasePath, "SELECT COUNT(*) FROM chunk_vector;", null);
			}

			// One vec0 table per model version, named by the store itself; count across whatever exists.
			using SqliteConnection connection = OpenWithVec(this.DatabasePath);

			int total = 0;

			foreach (string table in VecTables(connection))
			{
				using SqliteCommand command = connection.CreateCommand();
				command.CommandText = $"SELECT COUNT(*) FROM {table};";
				total += (int)(long)(command.ExecuteScalar() ?? 0L);
			}

			return total;
		}

		private static List<string> VecTables(SqliteConnection connection)
		{
			List<string> tables = [];

			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = """
				SELECT name FROM sqlite_master
				WHERE type = 'table' AND name LIKE 'vec_chunk_vector__%' AND sql LIKE 'CREATE VIRTUAL TABLE%';
				""";

			using SqliteDataReader reader = command.ExecuteReader();
			while (reader.Read())
			{
				tables.Add(reader.GetString(0));
			}

			return tables;
		}

		private static SqliteConnection OpenWithVec(string databasePath)
			=> FolderDatabaseConnection.OpenRead(databasePath, withVectorExtension: true);

		private static long Scalar(string databasePath, string sql, string? id)
		{
			using SqliteConnection connection = Open(databasePath);
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = sql;

			if (id is not null)
			{
				command.Parameters.AddWithValue("$id", id);
			}

			return (long)(command.ExecuteScalar() ?? 0L);
		}

		private static SqliteConnection Open(string databasePath)
		{
			SqliteConnection connection = new($"Data Source={databasePath}");
			connection.Open();

			return connection;
		}

		public void Dispose() => this.Service.Dispose();
	}
}
