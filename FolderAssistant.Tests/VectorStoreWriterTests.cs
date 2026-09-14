using System.Text.Json;
using FluentAssertions;
using FolderAssistant.Embedding;
using FolderAssistant.Indexing;
using FolderAssistant.Indexing.Scanning;
using FolderAssistant.Persistence;
using Microsoft.Data.Sqlite;

namespace FolderAssistant.Tests;

public sealed class VectorStoreWriterTests
{
	/// <summary>
	/// The point of the seam: the repository must have no vector-writing path of its own. A recording
	/// writer that stores nothing leaves <c>chunk_vector</c> empty while the chunk rows still land —
	/// which it could not do if the repository also wrote vectors directly.
	/// </summary>
	[Fact]
	public void Every_Vector_Goes_Through_The_Injected_Writer_And_No_Other_Path()
	{
		using TempFolder folder = new();
		String databasePath = BootstrapIn(folder);
		String fileId = RecordFile(databasePath, "a.md");

		RecordingVectorStoreWriter writer = new();

		IndexWriteSummary summary = new FolderIndexRepository(writer).Upsert(
			databasePath,
			new Dictionary<String, IReadOnlyList<ChunkMetadata>> { [fileId] = [Chunk("c1", 0), Chunk("c2", 1)] },
			new Dictionary<String, EmbeddingResult>
			{
				["c1"] = Embedding([1.0f, 0.0f, 0.0f]),
				["c2"] = Embedding([0.0f, 1.0f, 0.0f]),
			},
			Descriptor(3));

		summary.VectorsUpserted.Should().Be(2);
		writer.Calls.Should().HaveCount(2);
		writer.Calls.Select(static call => call.ChunkId).Should().Equal("c1", "c2");
		writer.Calls.Should().OnlyContain(call => call.ModelVersionId == "programmable-v1" && call.Dimension == 3);

		using SqliteConnection connection = Connect(databasePath);
		Count(connection, "SELECT COUNT(*) FROM chunk_manifest;").Should().Be(2);
		Count(connection, "SELECT COUNT(*) FROM chunk_vector;").Should().Be(0);
	}

	/// <summary>
	/// The writer is handed the caller's transaction, not one of its own, so a vector cannot survive a
	/// pass whose chunk rows were rolled back.
	/// </summary>
	[Fact]
	public void A_Vector_Written_Through_The_Seam_Is_Inside_The_Callers_Transaction()
	{
		using TempFolder folder = new();
		String databasePath = BootstrapIn(folder);
		String fileId = RecordFile(databasePath, "a.md");

		ThrowingVectorStoreWriter writer = new();

		FluentActions.Invoking(() => new FolderIndexRepository(writer).Upsert(
				databasePath,
				new Dictionary<String, IReadOnlyList<ChunkMetadata>> { [fileId] = [Chunk("c1", 0)] },
				new Dictionary<String, EmbeddingResult> { ["c1"] = Embedding([1.0f]) },
				Descriptor(1)))
			.Should().Throw<InvalidOperationException>();

		using SqliteConnection connection = Connect(databasePath);
		Count(connection, "SELECT COUNT(*) FROM chunk_manifest;").Should().Be(0);
		Count(connection, "SELECT COUNT(*) FROM file_manifest;")
			.Should().Be(1, "the file's record is the store's and was never part of this write");
	}

	[Fact]
	public void The_Default_Writer_Stores_The_Vector_As_A_Packed_Blob()
	{
		using TempFolder folder = new();
		String databasePath = BootstrapIn(folder);
		String fileId = RecordFile(databasePath, "a.md");

		new FolderIndexRepository().Upsert(
			databasePath,
			new Dictionary<String, IReadOnlyList<ChunkMetadata>> { [fileId] = [Chunk("c1", 0)] },
			new Dictionary<String, EmbeddingResult> { ["c1"] = Embedding([0.5f, -0.25f]) },
			Descriptor(2));

		using SqliteConnection connection = Connect(databasePath);
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT vector, vector_dimension FROM chunk_vector WHERE chunk_id = 'c1';";
		using SqliteDataReader reader = command.ExecuteReader();

		reader.Read().Should().BeTrue();

		// Eight bytes for two components, not a JSON array of them.
		Byte[] blob = (Byte[])reader.GetValue(0);
		blob.Should().HaveCount(8);
		VectorBlob.Unpack(blob).Should().Equal(0.5f, -0.25f);
		reader.GetInt32(1).Should().Be(2);
	}

	[Fact]
	public void A_Repository_Cannot_Be_Built_Without_A_Writer()
		=> FluentActions.Invoking(() => new FolderIndexRepository(null!))
			.Should().Throw<ArgumentNullException>();

	private sealed record WriteCall(String ChunkId, String ModelVersionId, Int32 Dimension);

	private sealed class RecordingVectorStoreWriter : IVectorStoreWriter
	{
		public List<WriteCall> Calls { get; } = [];

		public Boolean RequiresVectorExtension => false;

		public void EnsureSchema(
			SqliteConnection connection,
			SqliteTransaction transaction,
			String modelVersionId,
			Int32 vectorDimension)
		{
		}

		public void UpsertVector(
			SqliteConnection connection,
			SqliteTransaction transaction,
			String chunkId,
			String modelVersionId,
			IReadOnlyList<Single> vector,
			Int32 vectorDimension)
			=> this.Calls.Add(new WriteCall(chunkId, modelVersionId, vectorDimension));

		public void DeleteVectors(
			SqliteConnection connection,
			SqliteTransaction transaction,
			IReadOnlyList<String> chunkIds)
		{
		}
	}

	private sealed class ThrowingVectorStoreWriter : IVectorStoreWriter
	{
		public Boolean RequiresVectorExtension => false;

		public void EnsureSchema(
			SqliteConnection connection,
			SqliteTransaction transaction,
			String modelVersionId,
			Int32 vectorDimension)
		{
		}

		public void UpsertVector(
			SqliteConnection connection,
			SqliteTransaction transaction,
			String chunkId,
			String modelVersionId,
			IReadOnlyList<Single> vector,
			Int32 vectorDimension)
			=> throw new InvalidOperationException("The backend refused the vector.");

		public void DeleteVectors(
			SqliteConnection connection,
			SqliteTransaction transaction,
			IReadOnlyList<String> chunkIds)
		{
		}
	}

	private static String BootstrapIn(TempFolder folder)
		=> new FolderDatabaseBootstrapper().EnsureInitialized(folder.Path, new PersistenceConfig()).DatabasePath;

	/// <summary>A file's record is the store's, so it is written there first; the chunks reference it.</summary>
	private static String RecordFile(String databasePath, String relativePath)
	{
		new FolderIndexStore(databasePath)
			.ApplyAsync([new ReconciledChange(relativePath, FileDelta.Added, new FileRecord(relativePath, "filehash", 10, default))])
			.GetAwaiter()
			.GetResult();

		return FileIdentity.For(relativePath);
	}

	// The repository takes metadata, not text: what it stores is ids, offsets and hashes.
	private static ChunkMetadata Chunk(String chunkId, Int32 index)
		=> new(chunkId, index, index, index + 1, $"hash-{chunkId}");

	private static ModelDescriptor Descriptor(Int32 dimension)
		=> new("programmable-v1", "programmable", "programmable-embedding", dimension);

	private static EmbeddingResult Embedding(Single[] vector)
		=> new(vector, "programmable-v1", vector.Length, "programmable");

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
