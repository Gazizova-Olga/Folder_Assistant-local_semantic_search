using System.Text;
using FluentAssertions;
using FolderAssistant.Embedding;
using FolderAssistant.Indexing;
using FolderAssistant.Persistence;
using FolderAssistant.Retrieval;

namespace FolderAssistant.Tests;

/// <summary>
/// The Ollama embedder against a real local server, end to end through the <c>ollama-blob</c> profile.
///
/// <para>
/// Gated on the server being reachable with the model pulled, and skips cleanly otherwise — the same
/// arrangement the <c>sqlite-vec</c> tests use for their native binary. The gate is honest rather than
/// convenient: these are the only tests in the suite that can show the embedder produces *meaning*, and
/// nothing else can stand in for them. Everything else about this class is covered against a fake.
/// </para>
///
/// <para>
/// What is being demonstrated is the thing the placeholder embedder cannot do at all. Its vectors are a
/// character histogram, so it can only match text that looks alike; a query sharing no salient word with
/// the passage that answers it is invisible to it. That is the whole reason a real model is worth a
/// network dependency, and it is asserted here rather than assumed.
/// </para>
/// <para>
/// <strong>A skip here reports as a pass.</strong> xUnit 2 has no dynamic skip, so the convention in
/// this suite — the one <c>SqliteVecBackendTests</c> already uses for its native binary — is to return
/// early, and the runner cannot tell that apart from a test that ran. On a machine with no Ollama these
/// three assert nothing while showing green. That is stated rather than left to be discovered: this
/// repository has already had containment tests silently asserting nothing for weeks, and the lesson
/// was that a green run proves nothing unless you know what it covered.
/// </para>
/// </summary>
public sealed class OllamaLiveIntegrationTests
{
	private const String ModelVersionId = "qwen3-live-test-v1";

	/// <summary>The width qwen3-embedding:0.6b emits, and the configured default it must agree with.</summary>
	private const Int32 ExpectedDimension = 1024;

	/// <summary>
	/// Confirms the configured default width is the model's actual width. The vectorizer refuses a
	/// mismatch, so a wrong default would not corrupt anything — it would make the profile unusable, and
	/// this is what says which of the two is wrong.
	/// </summary>
	[Fact]
	public async Task The_Model_Emits_The_Width_The_Configuration_Defaults_To()
	{
		if (!TryCreateVectorizer(out OllamaEmbeddingVectorizer vectorizer))
		{
			return;
		}

		using (vectorizer)
		{
			IReadOnlyList<EmbeddingResult> results =
				await vectorizer.VectorizeAsync(["a short passage"], EmbeddingKind.Document);

			results.Should().ContainSingle();
			results[0].Vector.Count.Should().Be(ExpectedDimension);
			results[0].ProviderType.Should().Be("ollama");
		}
	}

	/// <summary>
	/// Retrieval by meaning rather than by wording. The query shares no salient term with the passage
	/// that answers it — "vehicle" and "drive to the office" against a document about a car and commuting
	/// — so a lexical or character-frequency embedder has nothing to match on.
	/// </summary>
	[Fact]
	public void A_Query_Retrieves_The_Passage_That_Answers_It_Without_Sharing_Its_Words()
	{
		if (!TryCreateVectorizer(out OllamaEmbeddingVectorizer vectorizer))
		{
			return;
		}

		using (vectorizer)
		{
			using TempFolder folder = new();
			WriteCorpus(folder);

			String databasePath = Bootstrap(folder);
			ModuleSet profile = CompositionProfiles.Resolve("ollama-blob");
			IndexingConfig config = LiveConfig();

			new FolderIndexingPipeline(
				vectorizer,
				profile.CreateVectorStoreWriter(),
				profile.CreateVectorStoreReader())
				.Run(folder.Path, databasePath, config);

			IReadOnlyList<RetrievalHit> hits = profile
				.CreateRetrievalQuery(vectorizer, profile.CreateVectorStoreReader(), null)
				.Search(databasePath, Query, new RetrievalOptions(TopK: 2));

			hits.Should().NotBeEmpty();
			hits[0].FilePath.Should().Be(
				"commuting.md",
				"the answer shares no salient word with the question, so only meaning can connect them");
		}
	}

	/// <summary>
	/// The contrast that makes the previous test mean something. The same corpus and the same query, put
	/// to the placeholder embedder, does not find the same passage — so the result above is the model
	/// working, not the corpus being easy.
	/// </summary>
	[Fact]
	public void The_Placeholder_Embedder_Cannot_Answer_The_Same_Query()
	{
		if (!TryCreateVectorizer(out OllamaEmbeddingVectorizer live))
		{
			return;
		}

		live.Dispose();

		using TempFolder folder = new();
		WriteCorpus(folder);

		String databasePath = Bootstrap(folder);
		IndexingConfig config = new() { ModelVersionId = "placeholder-live-contrast", VectorDimension = 64 };
		ProgrammableEmbeddingVectorizer placeholder = new(config.ModelVersionId, config.VectorDimension);

		new FolderIndexingPipeline(
			placeholder,
			new SqliteBlobVectorStoreWriter(),
			new SqliteBlobVectorStoreReader())
			.Run(folder.Path, databasePath, config);

		IReadOnlyList<RetrievalHit> hits = new CosineRetrievalQuery(placeholder)
			.Search(databasePath, Query, new RetrievalOptions(TopK: 2));

		hits.Should().NotBeEmpty();
		hits[0].FilePath.Should().NotBe(
			"commuting.md",
			"a character histogram has no way to connect a question to an answer that shares none of its words");
	}

	private const String Query = "which vehicle should I take to get to the office";

	/// <summary>
	/// Two documents that answer different questions, sharing no salient vocabulary with the query. The
	/// wording is chosen so that surface similarity points the wrong way if anything.
	/// </summary>
	private static void WriteCorpus(TempFolder folder)
	{
		File.WriteAllText(
			folder.Combine("commuting.md"),
			"The saloon car sits in the driveway. Most mornings it carries one passenger along the "
			+ "motorway into the city centre and back again in the evening.",
			Encoding.UTF8);

		File.WriteAllText(
			folder.Combine("cooking.md"),
			"Whisk the eggs into the flour, add sugar and melted butter, then bake the sponge until "
			+ "a skewer comes out clean and leave it to cool on a wire rack.",
			Encoding.UTF8);
	}

	/// <summary>
	/// Whether a local Ollama has the model, decided once for the whole class.
	///
	/// <para>
	/// Probing per test costs the full client retry budget per test on a machine with no server — three
	/// tests, three waits, for one answer that cannot change between them. The reachability question is
	/// a property of the machine, so it is asked once.
	/// </para>
	/// </summary>
	private static readonly Lazy<Boolean> ServerHasTheModel = new(Probe);

	/// <summary>
	/// Builds a vectorizer if a local Ollama has the model, and reports whether it did. The probe is a
	/// real embed: a reachable server that has not pulled the model would otherwise fail later, inside
	/// the test, and read as a product failure rather than a missing prerequisite.
	/// </summary>
	private static Boolean TryCreateVectorizer(out OllamaEmbeddingVectorizer vectorizer)
	{
		if (!ServerHasTheModel.Value)
		{
			vectorizer = null!;
			return false;
		}

		IndexingConfig config = LiveConfig();

		vectorizer = new OllamaEmbeddingVectorizer(
			config.OllamaEndpoint, config.OllamaModel, config.OllamaModelVersionId, config.OllamaEmbeddingDimension,
			TimeSpan.FromSeconds(config.OllamaTimeoutSeconds), config.AllowRemoteEmbeddingEndpoint);

		return true;
	}

	private static Boolean Probe()
	{
		IndexingConfig config = LiveConfig();

		using OllamaEmbeddingVectorizer candidate = new(
			config.OllamaEndpoint, config.OllamaModel, config.OllamaModelVersionId, config.OllamaEmbeddingDimension,
			TimeSpan.FromSeconds(config.OllamaTimeoutSeconds), config.AllowRemoteEmbeddingEndpoint);

		try
		{
			using CancellationTokenSource probeTimeout = new(TimeSpan.FromSeconds(30));

			candidate.VectorizeAsync(["probe"], EmbeddingKind.Document, probeTimeout.Token)
				.AsTask().GetAwaiter().GetResult();

			return true;
		}
		catch (Exception)
		{
			// No server, no model, or it could not answer in time. Not this suite's business to say which.
			return false;
		}
	}

	private static IndexingConfig LiveConfig()
		=> new()
		{
			ModelVersionId = ModelVersionId,
			VectorDimension = ExpectedDimension,
			OllamaModelVersionId = ModelVersionId,
		};

	private static String Bootstrap(TempFolder folder)
		=> new FolderDatabaseBootstrapper().EnsureInitialized(folder.Path, new PersistenceConfig()).DatabasePath;
}
