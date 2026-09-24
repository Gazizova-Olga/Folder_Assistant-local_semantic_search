using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using FolderAssistant.Embedding;
using FolderAssistant.Indexing;
using FolderAssistant.Persistence;
using FolderAssistant.Retrieval;
using Xunit.Abstractions;

namespace FolderAssistant.Tests;

/// <summary>
/// Measures whether an absolute score floor can tell a question this corpus answers from one it does
/// not — the premise a low-confidence short circuit rests on.
///
/// <para>
/// **This asserts nothing.** It is a measuring instrument, opt-in through <c>RELEVANCE_FLOOR_BENCH</c>,
/// and it reports distributions rather than a verdict. A floor is worth having only if the two
/// populations separate; the number that separates them is an output of the measurement, never an input
/// to it.
/// </para>
///
/// <para>
/// Both embedders are measured on the identical corpus and the identical queries, because the earlier
/// attempt at this question could not distinguish "the floor does nothing" from "this embedder scores
/// everything alike". The placeholder is the control: its vectors are a character histogram, so it is
/// expected to score an unrelated question as highly as a related one, and a harness that does not
/// reproduce that is measuring itself.
/// </para>
///
/// <para>
/// <strong>A skip reports as a pass.</strong> Without a reachable Ollama the live half returns early,
/// and xUnit 2 cannot tell that from a run — the same caveat <c>OllamaLiveIntegrationTests</c> carries.
/// </para>
/// </summary>
public sealed class RelevanceFloorBenchmark
{
	private readonly ITestOutputHelper _output;

	public RelevanceFloorBenchmark(ITestOutputHelper output) => this._output = output;

	/// <summary>
	/// Questions the corpus below genuinely answers. Worded to avoid quoting the documents, so a match
	/// is meaning rather than shared vocabulary.
	/// </summary>
	private static readonly String[] OnTopic =
	[
		"how do I get to work in the morning",
		"what should I feed a cat",
		"how is bread made at home",
		"what happens to plants without enough light",
		"how do I keep my bicycle running well",
	];

	/// <summary>
	/// Questions about domains the corpus contains nothing on. Not gibberish — a floor that only rejects
	/// nonsense would reject nothing a user actually types.
	/// </summary>
	private static readonly String[] OffTopic =
	[
		"what were the terms of the treaty of westphalia",
		"how does a semiconductor junction work",
		"what is the tax treatment of a capital loss",
		"which composer wrote the goldberg variations",
		"how do I apply for a mortgage",
	];

	[Fact]
	[SuppressMessage("Major Code Smell", "S2699:Tests should include assertions",
		Justification = "It is a measuring instrument, not a test, and it says so. Asserting a separation " +
			"here would fix in advance the number this exists to discover, and would fail the build on a " +
			"machine with no Ollama for reasons that have nothing to do with the code. It lives under [Fact] " +
			"because that is the runner already present, and returns immediately unless asked for.")]
	public void Measure_Whether_A_Score_Floor_Separates_Answerable_Questions_From_Unanswerable_Ones()
	{
		if (String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RELEVANCE_FLOOR_BENCH")))
		{
			return;
		}

		this.Report("placeholder (programmable-blob)", MeasureWithPlaceholder());

		Measurement[]? live = MeasureWithOllama();

		if (live is null)
		{
			this._output.WriteLine("");
			this._output.WriteLine("ollama: not reachable with the model pulled — live half not measured.");
			return;
		}

		this.Report("qwen3-embedding:0.6b (ollama-blob)", live);
	}

	private static Measurement[] MeasureWithPlaceholder()
	{
		using TempFolder folder = new();
		WriteCorpus(folder);

		String databasePath = Bootstrap(folder);
		IndexingConfig config = new() { ModelVersionId = "relevance-floor-placeholder", VectorDimension = 64 };
		ProgrammableEmbeddingVectorizer vectorizer = new(config.ModelVersionId, config.VectorDimension);

		new FolderIndexingPipeline(vectorizer, new SqliteBlobVectorStoreWriter(), new SqliteBlobVectorStoreReader())
			.Run(folder.Path, databasePath, config);

		return Measure(new CosineRetrievalQuery(vectorizer), databasePath);
	}

	private static Measurement[]? MeasureWithOllama()
	{
		IndexingConfig config = new();

		using OllamaEmbeddingVectorizer vectorizer = new(
			config.OllamaEndpoint, config.OllamaModel, config.OllamaModelVersionId, config.OllamaEmbeddingDimension,
			TimeSpan.FromSeconds(config.OllamaTimeoutSeconds), config.AllowRemoteEmbeddingEndpoint);

		try
		{
			using CancellationTokenSource probe = new(TimeSpan.FromSeconds(30));
			vectorizer.VectorizeAsync(["probe"], EmbeddingKind.Document, probe.Token).AsTask().GetAwaiter().GetResult();
		}
		catch (Exception)
		{
			return null;
		}

		using TempFolder folder = new();
		WriteCorpus(folder);

		String databasePath = Bootstrap(folder);
		IndexingConfig indexing = new()
		{
			ModelVersionId = config.OllamaModelVersionId,
			VectorDimension = config.OllamaEmbeddingDimension,
		};

		ModuleSet profile = CompositionProfiles.Resolve("ollama-blob");

		new FolderIndexingPipeline(vectorizer, profile.CreateVectorStoreWriter(), profile.CreateVectorStoreReader())
			.Run(folder.Path, databasePath, indexing);

		return Measure(profile.CreateRetrievalQuery(vectorizer, profile.CreateVectorStoreReader(), null), databasePath);
	}

	/// <summary>
	/// The best score each query can reach. A short circuit fires on the top hit, so the top hit is what
	/// has to separate — an average over the whole result set would describe a different decision.
	/// </summary>
	private static Measurement[] Measure(IRetrievalQuery query, String databasePath)
	{
		List<Measurement> measurements = [];

		foreach (String text in OnTopic)
		{
			measurements.Add(new Measurement(text, Answerable: true, BestScore(query, databasePath, text)));
		}

		foreach (String text in OffTopic)
		{
			measurements.Add(new Measurement(text, Answerable: false, BestScore(query, databasePath, text)));
		}

		return [.. measurements];
	}

	private static Double BestScore(IRetrievalQuery query, String databasePath, String text)
	{
		IReadOnlyList<RetrievalHit> hits = query.Search(databasePath, text, new RetrievalOptions(TopK: 5));

		return hits.Count == 0 ? Double.NaN : hits[0].Score;
	}

	private void Report(String label, Measurement[] measurements)
	{
		Double[] answerable = [.. measurements.Where(m => m.Answerable).Select(m => m.TopScore).Order()];
		Double[] unanswerable = [.. measurements.Where(m => !m.Answerable).Select(m => m.TopScore).Order()];

		this._output.WriteLine("");
		this._output.WriteLine($"=== {label} ===");
		this._output.WriteLine("");

		foreach (Measurement measurement in measurements)
		{
			String bucket = measurement.Answerable ? "answerable  " : "unanswerable";
			this._output.WriteLine($"  {bucket}  {measurement.TopScore,7:F4}  {measurement.Query}");
		}

		this._output.WriteLine("");
		this._output.WriteLine($"  answerable   min {answerable[0]:F4}  max {answerable[^1]:F4}");
		this._output.WriteLine($"  unanswerable min {unanswerable[0]:F4}  max {unanswerable[^1]:F4}");

		// The gap between the worst question the corpus can answer and the best it cannot. Positive means
		// some threshold separates the two populations completely; negative means none does, whatever
		// number is chosen.
		Double margin = answerable[0] - unanswerable[^1];

		this._output.WriteLine($"  separation   {margin:F4} " +
			$"({(margin > 0 ? "separable" : "NOT separable — the populations overlap")})");

		if (margin > 0)
		{
			this._output.WriteLine($"  a floor anywhere in ({unanswerable[^1]:F4}, {answerable[0]:F4}] separates them");
		}

		// What the upstream constant would actually do here, which is a different question from whether a
		// floor is possible at all.
		Int32 rejectedAnswerable = answerable.Count(score => score < 0.2);
		Int32 rejectedUnanswerable = unanswerable.Count(score => score < 0.2);

		this._output.WriteLine("");
		this._output.WriteLine($"  at a 0.2 floor: rejects {rejectedUnanswerable}/{unanswerable.Length} unanswerable, " +
			$"{rejectedAnswerable}/{answerable.Length} answerable");
	}

	private static String Bootstrap(TempFolder folder)
		=> new FolderDatabaseBootstrapper().EnsureInitialized(folder.Path, new PersistenceConfig()).DatabasePath;

	/// <summary>
	/// Five documents on unrelated everyday subjects. Small and plain on purpose: the question is whether
	/// an off-domain query scores low, and a corpus of one topic could not show that.
	/// </summary>
	private static void WriteCorpus(TempFolder folder)
	{
		File.WriteAllText(folder.Combine("commuting.md"),
			"The train leaves at seven and takes twenty minutes to reach the centre. "
			+ "Walking from the station adds another ten minutes on foot. "
			+ "Driving is slower once the roads fill up, and parking near the office is expensive.");

		File.WriteAllText(folder.Combine("cats.md"),
			"A kitten needs several small meals through the day, while an adult is fine with two. "
			+ "Wet food helps with hydration. Fresh water should always be available, "
			+ "and milk upsets most adult cats rather than helping them.");

		File.WriteAllText(folder.Combine("baking.md"),
			"Mix flour, water, salt and yeast, then leave the dough to rise until it doubles. "
			+ "Knead it briefly, shape the loaf, and let it prove a second time. "
			+ "A hot oven and a tray of steam give the crust its colour.");

		File.WriteAllText(folder.Combine("houseplants.md"),
			"Leaves turn pale and stems stretch towards the window when a plant is starved of light. "
			+ "Growth slows, new leaves come in smaller, and the lower ones drop. "
			+ "Moving the pot somewhere brighter usually reverses it over a few weeks.");

		File.WriteAllText(folder.Combine("bicycle.md"),
			"Keep the chain clean and lightly oiled, and check tyre pressure before a long ride. "
			+ "Brake pads wear down and need replacing once the grooves disappear. "
			+ "A yearly service catches the wear you cannot see.");
	}

	private sealed record Measurement(String Query, Boolean Answerable, Double TopScore);
}
