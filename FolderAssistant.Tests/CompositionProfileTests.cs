using System.Text;
using FluentAssertions;
using FolderAssistant.Embedding;
using FolderAssistant.Indexing;
using FolderAssistant.Persistence;
using FolderAssistant.Retrieval;

namespace FolderAssistant.Tests;

/// <summary>
/// Profile selection: which bundle of implementations the composition root builds, and what happens
/// when the name it is given cannot be honoured.
///
/// <para>
/// The interesting property is not that a name maps to a set of constructors — it is that the two
/// halves of a bundle always agree. A writer and reader that disagree about where vectors live do
/// not fail; they report that nothing has ever been embedded, so the folder re-embeds on every run
/// while the index still looks correct from the outside. That is why these are enumerated bundles
/// rather than per-module switches, and it is what the end-to-end cases below actually check.
/// </para>
/// </summary>
public sealed class CompositionProfileTests
{
	private const String ModelVersionId = "profile-test-v1";

	/// <summary>
	/// A corpus-fitted profile needs a target rank well below the rank of the corpus, or the
	/// reduction degenerates back to lexical matching. Three short documents will not carry 64
	/// dimensions, so the fittable profiles are given a rank their fixture can actually support.
	/// </summary>
	private const Int32 FittedDimension = 3;

	private const Int32 FixedDimension = 64;

	/// <summary>
	/// The default must be the combination that runs wherever the managed code runs. A default with
	/// a native dependency would turn an unsupported platform into a startup failure for someone who
	/// never chose a backend at all.
	/// </summary>
	[Fact]
	public void The_Default_Is_The_Fitted_Embedder_Over_The_Native_Store_And_Its_Fallback_Its_Blob_Twin()
	{
		CompositionProfiles.Default.Should().Be("lsa-vec");
		CompositionProfiles.DefaultFallback.Should().Be("lsa-blob");
		CompositionProfiles.Resolve(CompositionProfiles.DefaultFallback).IsAvailable()
			.Should().BeTrue("the fallback is what has to run on every platform");
	}

	/// <summary>
	/// The one fallback there is. Nobody named a profile, the default's native store has no binary here,
	/// so its blob twin runs — and the note says so, naming both, because a fallback nobody can see is
	/// the thing SPEC-000 forbids.
	/// </summary>
	[Fact]
	public void The_Unnamed_Default_Falls_Back_To_Its_Blob_Twin_Where_The_Native_Store_Cannot_Load_And_Says_So()
	{
		DefaultResolution here = CompositionProfiles.ResolveDefault(static profile => profile.IsAvailable());
		DefaultResolution elsewhere = CompositionProfiles.ResolveDefault(static profile => profile.Name == "lsa-blob");

		here.Profile.Name.Should().Be(SqliteVecExtension.IsAvailable ? "lsa-vec" : "lsa-blob");
		(here.FallbackNote is null).Should().Be(SqliteVecExtension.IsAvailable, "the note exists exactly when the fallback ran");
		elsewhere.Profile.Name.Should().Be("lsa-blob");
		elsewhere.FallbackNote.Should().Contain("lsa-vec").And.Contain("lsa-blob").And.Contain("FolderAssistant:Profile");
	}

	[Fact]
	public void The_Unnamed_Default_Is_The_Native_Store_Where_It_Can_Load()
	{
		DefaultResolution resolution = CompositionProfiles.ResolveDefault(static _ => true);

		resolution.Profile.Name.Should().Be("lsa-vec");
		resolution.FallbackNote.Should().BeNull();
	}

	/// <summary>
	/// The rule the fallback must not erode: a profile the operator named never falls back, whatever the
	/// platform. The same predicate that sends the unnamed default to its twin throws for the named one.
	/// </summary>
	[Fact]
	public void A_Named_Profile_Never_Falls_Back()
	{
		Action resolve = () => CompositionProfiles.Resolve("lsa-vec", static profile => profile.Name == "lsa-blob");

		resolve.Should().Throw<PlatformNotSupportedException>()
			.Which.Message.Should().Contain("lsa-vec").And.Contain("lsa-blob");
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void An_Unset_Profile_Name_Resolves_The_Platforms_Default(String? name)
		=> CompositionProfiles.Resolve(name).Name.Should().Be(CompositionProfiles.ResolveDefault().Profile.Name);

	/// <summary>
	/// Configuration is hand-written, so casing is not a distinction worth failing on. It is the one
	/// mismatch tolerated rather than thrown at, and it is safe because it cannot resolve to a
	/// different profile than the one named.
	/// </summary>
	[Fact]
	public void A_Profile_Name_Is_Matched_Without_Regard_To_Case()
		=> CompositionProfiles.Resolve("PROGRAMMABLE-BLOB").Name.Should().Be("programmable-blob");

	/// <summary>
	/// The message has to carry the valid names. An operator who mistyped one has no other way to
	/// discover the set, which is enumerated in code precisely so that it is closed.
	/// </summary>
	[Fact]
	public void An_Unknown_Profile_Throws_And_Names_The_Ones_That_Exist()
	{
		Action resolve = () => CompositionProfiles.Resolve("postgres-hnsw");

		resolve.Should().Throw<InvalidOperationException>()
			.Which.Message.Should().Contain("postgres-hnsw").And.Contain(CompositionProfiles.Default);
	}

	/// <summary>
	/// The property that matters more than either throw on its own: whatever comes back can actually
	/// run. A fallback to the default would satisfy the caller and then serve answers out of a
	/// different embedding space than the one named in configuration.
	/// </summary>
	[Fact]
	public void Resolve_Never_Hands_Back_A_Profile_That_Cannot_Run_Here()
	{
		foreach (String name in CompositionProfiles.Names)
		{
			ModuleSet? resolved;

			try
			{
				resolved = CompositionProfiles.Resolve(name);
			}
			catch (PlatformNotSupportedException)
			{
				// The honest outcome for a profile whose native binary is missing here.
				continue;
			}

			resolved.IsAvailable().Should().BeTrue($"{name} was returned rather than refused");
		}
	}

	[Theory]
	[InlineData("programmable-blob")]
	[InlineData("programmable-vec")]
	[InlineData("lsa-blob")]
	[InlineData("lsa-vec")]
	[InlineData("ollama-blob")]
	[InlineData("ollama-vec")]
	public void Every_Available_Profile_Composes_All_Four_Modules(String name)
	{
		if (!TryResolve(name, out ModuleSet profile))
		{
			return;
		}

		IVectorizer vectorizer = profile.CreateVectorizer(ConfigFor(name));
		IVectorStoreReader reader = profile.CreateVectorStoreReader();

		vectorizer.Should().NotBeNull();
		profile.CreateVectorStoreWriter().Should().NotBeNull();
		reader.Should().NotBeNull();
		profile.CreateRetrievalQuery(vectorizer, reader, null).Should().NotBeNull();
	}

	/// <summary>
	/// The bundle end to end: index through the profile's writer, then retrieve through its reader
	/// and strategy. A mismatched pair would surface here as nothing being found — the same silence
	/// it would produce in production.
	///
	/// <para>
	/// The assertion is that a vehicle query returns a vehicle document rather than the unrelated
	/// one. It deliberately does not pin *which* of the two: the corpus-fitted profiles exist to
	/// treat those documents as near-synonymous, so demanding a particular one would assert against
	/// the property that family is built for.
	/// </para>
	/// <para>
	/// The <c>ollama-*</c> profiles are deliberately absent here. They compose (above) but cannot be
	/// exercised end to end without a live model server, and a test that silently skips whenever one is
	/// missing would report success for a path nobody ran.
	/// </para>
	/// </summary>
	[Theory]
	[InlineData("programmable-blob")]
	[InlineData("programmable-vec")]
	[InlineData("lsa-blob")]
	[InlineData("lsa-vec")]
	public void Every_Available_Profile_Indexes_And_Retrieves_Through_Its_Own_Pair(String name)
	{
		if (!TryResolve(name, out ModuleSet profile))
		{
			return;
		}

		using TempFolder folder = new();
		WriteCorpus(folder);

		String databasePath = Bootstrap(folder);
		IndexingConfig config = ConfigFor(name);

		// One instance, used to index and then to query, which is what the composition root does.
		IVectorizer vectorizer = profile.CreateVectorizer(config);

		new FolderIndexingPipeline(
			vectorizer,
			profile.CreateVectorStoreWriter(),
			profile.CreateVectorStoreReader())
			.Run(folder.Path, databasePath, config);

		IReadOnlyList<RetrievalHit> hits = profile
			.CreateRetrievalQuery(vectorizer, profile.CreateVectorStoreReader(), null)
			.Search(databasePath, "the ferry crosses the harbour each morning", new RetrievalOptions(TopK: 1));

		hits.Should().ContainSingle("the profile's reader must look where its writer wrote");
		hits[0].FilePath.Should().BeOneOf("harbour.md", "port.md");
	}

	/// <summary>
	/// Why the composition root registers the vectorizer as one shared instance rather than
	/// constructing one per consumer.
	///
	/// <para>
	/// For a corpus-fitted implementation the fit lives on the instance: the pipeline fits it while
	/// indexing, and a query embedded by any other instance would land in a different space than the
	/// vectors it is compared against. A second instance from the same profile is not equivalent to
	/// the first, and this pins that it is not.
	/// </para>
	/// </summary>
	[Fact]
	public void A_Corpus_Fitted_Profile_Must_Query_With_The_Instance_That_Indexed()
	{
		ModuleSet profile = CompositionProfiles.Resolve("lsa-blob");

		using TempFolder folder = new();
		WriteCorpus(folder);

		String databasePath = Bootstrap(folder);
		IndexingConfig config = ConfigFor("lsa-blob");

		IVectorizer indexed = profile.CreateVectorizer(config);

		new FolderIndexingPipeline(
			indexed,
			profile.CreateVectorStoreWriter(),
			profile.CreateVectorStoreReader())
			.Run(folder.Path, databasePath, config);

		RetrievalOptions options = new(TopK: 1);
		const String Query = "the ferry crosses the harbour each morning";

		profile.CreateRetrievalQuery(indexed, profile.CreateVectorStoreReader(), null)
			.Search(databasePath, Query, options)
			.Should().NotBeEmpty("the fitted instance is the one that embedded the corpus");

		IVectorizer unfitted = profile.CreateVectorizer(config);

		FluentActions
			.Invoking(() => profile.CreateRetrievalQuery(unfitted, profile.CreateVectorStoreReader(), null)
				.Search(databasePath, Query, options))
			.Should().Throw<InvalidOperationException>(
				"a fresh instance carries no fit, and answering anyway would be the silent failure");
	}

	/// <summary>
	/// Resolves <paramref name="name"/>, or reports that this platform cannot run it. Only the
	/// missing-native-binary case is skipped, so a genuine resolution fault still surfaces.
	/// </summary>
	private static Boolean TryResolve(String name, out ModuleSet profile)
	{
		try
		{
			profile = CompositionProfiles.Resolve(name);
			return true;
		}
		catch (PlatformNotSupportedException)
		{
			profile = null!;
			return false;
		}
	}

	/// <summary>
	/// Two documents about the same thing in different words, and one about something else. The
	/// shared vocabulary is what gives a corpus-fitted profile anything to reduce.
	/// </summary>
	private static void WriteCorpus(TempFolder folder)
	{
		File.WriteAllText(
			folder.Combine("harbour.md"),
			"the ferry crosses the harbour each morning the ferry carries cars across the harbour",
			Encoding.UTF8);

		File.WriteAllText(
			folder.Combine("port.md"),
			"the boat crosses the port each morning the boat carries cars across the port",
			Encoding.UTF8);

		File.WriteAllText(
			folder.Combine("orchard.md"),
			"apple pear plum orchard the orchard grows apple and pear trees in rows",
			Encoding.UTF8);
	}

	private static IndexingConfig ConfigFor(String profileName)
		=> new()
		{
			ModelVersionId = ModelVersionId,
			VectorDimension = profileName.StartsWith("lsa", StringComparison.OrdinalIgnoreCase)
				? FittedDimension
				: FixedDimension,
		};

	private static String Bootstrap(TempFolder folder)
		=> new FolderDatabaseBootstrapper().EnsureInitialized(folder.Path, new PersistenceConfig()).DatabasePath;
}
