using System.Text;
using FluentAssertions;
using FolderAssistant.Embedding;
using FolderAssistant.Indexing;
using FolderAssistant.Persistence;
using FolderAssistant.Retrieval;

namespace FolderAssistant.Tests;

/// <summary>
/// One suite, run identically against every vector store, parameterised over the composition
/// profiles that pair a store with its retrieval strategy.
///
/// <para>
/// It exists because per-backend tests had already let a real defect through. The question "has this
/// file been embedded?" was answered against <c>chunk_vector</c> — which the blob store fills and the
/// <c>vec0</c> store does not — so the blob store's own tests passed while, under the vec store,
/// every file would have re-embedded on every run, silently, with the index still looking correct.
/// A test written against one implementation can only assert what that implementation happens to do.
/// Only a test run against all of them asserts what the contract requires.
/// </para>
///
/// <para>
/// A new backend is added to <see cref="Backends"/> and inherits the whole suite. Where the native
/// extension is unavailable, the vec parameterisations skip rather than fail — that is the honest
/// outcome on a platform the binary does not ship for.
/// </para>
/// </summary>
public sealed class VectorStoreContractTests
{
	private const String ModelVersionId = "contract-v1";
	private const Int32 Dimension = 64;

	/// <summary>
	/// Deliberately the two <em>programmable</em> profiles. The embedder is deterministic and needs no
	/// corpus fit, so any difference these tests find belongs to the store, which is what is under
	/// test here.
	/// </summary>
	public static TheoryData<String> Backends => new() { "programmable-blob", "programmable-vec" };

	[Theory]
	[MemberData(nameof(Backends))]
	public void An_Exact_Text_Query_Ranks_Its_Own_Chunk_First(String profileName)
	{
		if (!Fixture.TryCreate(profileName, out Fixture fixture))
		{
			return;
		}

		using (fixture)
		{
			IReadOnlyList<RetrievalHit> hits = fixture.Search(Fixture.CakeText, TopK: 3);

			hits.Should().NotBeEmpty();
			hits[0].FilePath.Should().Be("cake.md");
		}
	}

	[Theory]
	[MemberData(nameof(Backends))]
	public void TopK_Bounds_The_Result_Set(String profileName)
	{
		if (!Fixture.TryCreate(profileName, out Fixture fixture))
		{
			return;
		}

		using (fixture)
		{
			fixture.Search(Fixture.CakeText, TopK: 1).Should().HaveCount(1);
			fixture.Search(Fixture.CakeText, TopK: 2).Should().HaveCount(2);
		}
	}

	/// <summary>
	/// Zero is rejected rather than quietly returning nothing. An empty result is a legitimate answer
	/// to a real query, so a store that conflated the two would let a caller's arithmetic bug read as
	/// "the index knows nothing about this".
	/// </summary>
	[Theory]
	[MemberData(nameof(Backends))]
	public void TopK_Of_Zero_Is_Rejected(String profileName)
	{
		if (!Fixture.TryCreate(profileName, out Fixture fixture))
		{
			return;
		}

		using (fixture)
		{
			FluentActions.Invoking(() => fixture.Search(Fixture.CakeText, TopK: 0))
				.Should().Throw<ArgumentOutOfRangeException>();
		}
	}

	/// <summary>
	/// Chunks store no text, so a hit is only useful if it says where to read the text back from.
	/// </summary>
	[Theory]
	[MemberData(nameof(Backends))]
	public void Hits_Carry_The_Source_Location_Of_Their_Chunk(String profileName)
	{
		if (!Fixture.TryCreate(profileName, out Fixture fixture))
		{
			return;
		}

		using (fixture)
		{
			RetrievalHit hit = fixture.Search(Fixture.CakeText, TopK: 1)[0];

			hit.ChunkId.Should().NotBeNullOrWhiteSpace();
			hit.FilePath.Should().Be("cake.md");
			hit.ChunkIndex.Should().BeGreaterThanOrEqualTo(0);
			hit.TokenEnd.Should().BeGreaterThan(hit.TokenStart);
		}
	}

	/// <summary>
	/// Scoring across embedding spaces is meaningless, so a query embedded under one model version
	/// must not reach vectors written under another.
	/// </summary>
	[Theory]
	[MemberData(nameof(Backends))]
	public void A_Model_Version_That_Was_Never_Indexed_Returns_No_Hits(String profileName)
	{
		if (!Fixture.TryCreate(profileName, out Fixture fixture))
		{
			return;
		}

		using (fixture)
		{
			fixture.SearchAsModelVersion("some-other-model", Fixture.CakeText, TopK: 3)
				.Should().BeEmpty();
		}
	}

	/// <summary>
	/// The defect this suite was built to catch, reproduced on demand.
	///
	/// <para>
	/// A second pass over an unchanged folder must embed nothing. Answering "does this file already
	/// have vectors?" against a table only one backend writes makes the other backend re-embed the
	/// whole corpus every run — and nothing reports it, because the index it produces is correct.
	/// </para>
	/// </summary>
	[Theory]
	[MemberData(nameof(Backends))]
	public void Reindexing_An_Unchanged_Folder_Embeds_Nothing(String profileName)
	{
		if (!Fixture.TryCreate(profileName, out Fixture fixture))
		{
			return;
		}

		using (fixture)
		{
			IndexingResult second = fixture.Reindex();

			second.VectorsIndexed.Should().Be(0, "nothing in the folder changed");
			second.FilesUnchanged.Should().Be(2);
		}
	}

	[Theory]
	[MemberData(nameof(Backends))]
	public void Deleting_A_File_Removes_Its_Vectors(String profileName)
	{
		if (!Fixture.TryCreate(profileName, out Fixture fixture))
		{
			return;
		}

		using (fixture)
		{
			File.Delete(fixture.Folder.Combine("cake.md"));
			fixture.Reindex().FilesDeleted.Should().Be(1);

			fixture.Search(Fixture.CakeText, TopK: 3)
				.Should().NotContain(hit => hit.FilePath == "cake.md",
					"a deleted file's vectors must not survive the pass that noticed it was gone");
		}
	}

	/// <summary>
	/// A chunk id is content-addressed, so editing a file produces new ids for the slots it occupies.
	/// The vectors of the superseded chunks have to go, or the file is retrievable at two versions at
	/// once and the older one never expires.
	/// </summary>
	[Theory]
	[MemberData(nameof(Backends))]
	public void Editing_A_File_Retires_The_Vectors_Of_Its_Superseded_Chunks(String profileName)
	{
		if (!Fixture.TryCreate(profileName, out Fixture fixture))
		{
			return;
		}

		using (fixture)
		{
			Int32 before = fixture.CountVectors();

			File.WriteAllText(
				fixture.Folder.Combine("cake.md"),
				"lemon drizzle sponge with poppy seeds and a thin sugar glaze",
				Encoding.UTF8);

			fixture.Reindex();

			fixture.CountVectors().Should().Be(before, "the edited file replaced its chunks rather than adding to them");
			fixture.Search(Fixture.CakeText, TopK: 3)
				.Should().NotContain(hit => hit.ChunkId == fixture.OriginalCakeChunkId);
		}
	}

	/// <summary>
	/// Whatever the storage format, the numbers that come back are the numbers that went in. A store
	/// that lost precision would still rank plausibly, which is exactly why it needs asserting.
	/// </summary>
	[Theory]
	[MemberData(nameof(Backends))]
	public void Vectors_Round_Trip_Through_The_Store_Unchanged(String profileName)
	{
		if (!Fixture.TryCreate(profileName, out Fixture fixture))
		{
			return;
		}

		using (fixture)
		{
			IReadOnlyList<StoredVector> stored = fixture.Reader.ReadVectorsByModelVersion(fixture.DatabasePath, ModelVersionId);

			stored.Should().NotBeEmpty();
			stored.Should().OnlyContain(vector => vector.Vector.Count == Dimension);

			IReadOnlyList<Single> expected = fixture.Vectorizer
				.Vectorize([Fixture.CakeText], EmbeddingKind.Document)[0].Vector;

			StoredVector match = stored.Single(vector => vector.ChunkId == fixture.OriginalCakeChunkId);

			for (Int32 i = 0; i < expected.Count; i++)
			{
				match.Vector[i].Should().BeApproximately(expected[i], 1e-6f);
			}
		}
	}

	/// <summary>
	/// An indexed folder under one profile, plus the pieces a test needs to interrogate it. Creating
	/// it reports whether this platform can run the profile at all, so each case skips in one place
	/// rather than repeating the check.
	/// </summary>
	private sealed class Fixture : IDisposable
	{
		public const String CakeText = "chocolate cake recipe with butter and sugar";
		private const String EngineText = "diesel engine maintenance torque and pistons";

		private readonly ModuleSet _profile;

		private Fixture(ModuleSet profile)
		{
			this._profile = profile;
			this.Folder = new TempFolder();

			File.WriteAllText(this.Folder.Combine("cake.md"), CakeText, Encoding.UTF8);
			File.WriteAllText(this.Folder.Combine("engine.md"), EngineText, Encoding.UTF8);

			this.DatabasePath = new FolderDatabaseBootstrapper()
				.EnsureInitialized(this.Folder.Path, new PersistenceConfig())
				.DatabasePath;

			this.Vectorizer = profile.CreateVectorizer(Config());
			this.Reader = profile.CreateVectorStoreReader();

			this.Reindex();

			this.OriginalCakeChunkId = this.Search(CakeText, TopK: 1)[0].ChunkId;
		}

		public TempFolder Folder { get; }

		public String DatabasePath { get; }

		public IVectorizer Vectorizer { get; }

		public IVectorStoreReader Reader { get; }

		/// <summary>The chunk id of the cake file as first indexed, for assertions about supersession.</summary>
		public String OriginalCakeChunkId { get; }

		public static Boolean TryCreate(String profileName, out Fixture fixture)
		{
			try
			{
				fixture = new Fixture(CompositionProfiles.Resolve(profileName));
				return true;
			}
			catch (PlatformNotSupportedException)
			{
				fixture = null!;
				return false;
			}
		}

		public IndexingResult Reindex()
			=> new FolderIndexingPipeline(
				this.Vectorizer,
				this._profile.CreateVectorStoreWriter(),
				this._profile.CreateVectorStoreReader())
				.Run(this.Folder.Path, this.DatabasePath, Config());

		public IReadOnlyList<RetrievalHit> Search(String query, Int32 TopK)
			=> this._profile
				.CreateRetrievalQuery(this.Vectorizer, this._profile.CreateVectorStoreReader(), null)
				.Search(this.DatabasePath, query, new RetrievalOptions(TopK));

		/// <summary>Searches as a vectorizer belonging to a different model version would.</summary>
		public IReadOnlyList<RetrievalHit> SearchAsModelVersion(String modelVersionId, String query, Int32 TopK)
		{
			IVectorizer other = this._profile.CreateVectorizer(Config() with { ModelVersionId = modelVersionId });

			return this._profile
				.CreateRetrievalQuery(other, this._profile.CreateVectorStoreReader(), null)
				.Search(this.DatabasePath, query, new RetrievalOptions(TopK));
		}

		public Int32 CountVectors()
			=> this.Reader.ReadVectorsByModelVersion(this.DatabasePath, ModelVersionId).Count;

		public void Dispose() => this.Folder.Dispose();

		private static IndexingConfig Config()
			=> new() { ModelVersionId = ModelVersionId, VectorDimension = Dimension };
	}
}
