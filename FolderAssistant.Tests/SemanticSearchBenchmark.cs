using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using FolderAssistant.Embedding;
using FolderAssistant.Embedding.Lsa;
using FolderAssistant.Indexing;
using FolderAssistant.Persistence;
using FolderAssistant.Retrieval;
using Xunit.Abstractions;

namespace FolderAssistant.Tests;

/// <summary>
/// Measures the two things a composition profile is chosen on, and measures them separately because they
/// are properties of different halves of it: the <em>embedder</em> decides whether the right document comes
/// back at all, and the <em>backend</em> decides how long that takes.
///
/// <para>
/// Keeping them apart is the point. A profile is a bundle, so a single "which profile is best" number would
/// blend an accuracy result and a latency result into something that cannot be acted on — and the promotion
/// questions the specs actually ask are separate ones: whether a real embedder earns its network dependency
/// (<c>SPEC-160</c>, <c>SPEC-162</c>), whether a corpus-fitted one captures enough synonymy to be worth
/// fitting (<c>SPEC-161</c>), and whether the native k-NN backend earns its per-platform binary
/// (<c>SPEC-131</c>). The last of those also needs the two backends to be checked for *agreement*: sharing
/// the same vectors and the same metric, they should rank a query identically, and a difference would mean
/// one of them is wrong rather than faster.
/// </para>
///
/// <para>
/// <strong>Nothing here asserts.</strong> Timings are machine- and load-dependent, the real embedder needs a
/// live local server, and a corpus of this size cannot support a threshold — so an assertion would either
/// fix in advance the number this exists to discover, or fail a build for reasons that have nothing to do
/// with the code. It is opt-in: set <c>RUN_SEMANTIC_BENCHMARK</c> to run it, and it returns immediately
/// otherwise. Results are written under <c>docs/benchmarks/</c>.
/// </para>
///
/// <para>
/// The corpus is hand-written and hand-labelled, and the queries deliberately avoid the distinctive words of
/// the document each one should find. That is what makes the measurement mean anything: against a corpus
/// where the query quotes its target, a model with no semantics at all scores well, and the number stops
/// separating the thing it was meant to separate.
/// </para>
/// </summary>
public sealed class SemanticSearchBenchmark
{
	private const String GateVariable = "RUN_SEMANTIC_BENCHMARK";

	/// <summary>
	/// Documents in the generated corpus for the accuracy comparison. Large enough that a corpus-fitted
	/// embedder has something to fit — a handful of documents gives LSA nothing to find — and small enough
	/// that a local CPU-only embedding server finishes the run in minutes rather than hours.
	/// </summary>
	private const Int32 GeneratedCorpusSize = 300;

	/// <summary>
	/// Target rank for the corpus-fitted embedder. Well below the rank of the generated corpus on purpose:
	/// at or near full rank the reduction stops merging related terms and collapses back to lexical
	/// matching, which is the failure <c>SPEC-161</c> exists to prevent.
	/// </summary>
	private const Int32 LsaDimension = 32;

	/// <summary>Corpus sizes for the backend latency sweep. One point cannot show a trend.</summary>
	private static readonly Int32[] ScaleSizes = [500, 2000, 8000];

	private readonly ITestOutputHelper _output;

	public SemanticSearchBenchmark(ITestOutputHelper output) => this._output = output;

	// ── 1. the curated corpus: which embedder finds the right document ────────────────────────────

	[Fact]
	[SuppressMessage("Major Code Smell", "S2699:Tests should include assertions",
		Justification = "A measuring instrument, not a test, and the class comment says why it asserts nothing. " +
			"It runs under [Fact] because that is the runner already present, and returns immediately unless " +
			"the environment asks for it.")]
	public void Measure_Retrieval_Accuracy_Across_The_Available_Profiles()
	{
		if (!Requested())
		{
			return;
		}

		LabelledQuery[] queries = LoadLabelledQueries();
		String[] documents = Directory.GetFiles(CorpusDirectory(), "*.txt");

		this.Log($"curated corpus: {documents.Length} documents, {queries.Length} labelled queries");

		List<ProfileAccuracy> results = [];

		foreach (String profileName in AvailableProfiles())
		{
			this.Log($"running {profileName} ...");
			results.Add(MeasureCurated(profileName, documents, queries));
		}

		foreach (ProfileAccuracy result in results)
		{
			this.Log($"  {result.Profile,-18} R@1={result.RecallAt1:P0} R@3={result.RecallAt3:P0} " +
				$"R@5={result.RecallAt5:P0} MRR={result.MeanReciprocalRank:F3} " +
				$"index={result.IndexMs} ms query={result.MeanQueryMs:F1} ms");
		}

		this.ReportCurated(results, documents.Length, queries.Length);
	}

	private static ProfileAccuracy MeasureCurated(String profileName, String[] documents, LabelledQuery[] queries)
	{
		ModuleSet profile = CompositionProfiles.Resolve(profileName);

		using TempFolder folder = new();
		foreach (String document in documents)
		{
			File.Copy(document, folder.Combine(Path.GetFileName(document)));
		}

		IndexingConfig config = new();
		IVectorizer vectorizer = profile.CreateVectorizer(config);

		try
		{
			String databasePath = Bootstrap(folder);

			Stopwatch watch = Stopwatch.StartNew();
			new FolderIndexingPipeline(vectorizer, profile.CreateVectorStoreWriter(), profile.CreateVectorStoreReader())
				.Run(folder.Path, databasePath, config);
			Int64 indexMs = watch.ElapsedMilliseconds;

			IRetrievalQuery query = profile.CreateRetrievalQuery(
				vectorizer, profile.CreateVectorStoreReader(), null);

			Double recallAt1 = 0;
			Double recallAt3 = 0;
			Double recallAt5 = 0;
			Double reciprocal = 0;
			List<Double> latencies = [];

			foreach (LabelledQuery labelled in queries)
			{
				watch.Restart();
				IReadOnlyList<RetrievalHit> hits = query.Search(databasePath, labelled.Text, new RetrievalOptions(TopK: 10));
				latencies.Add(watch.Elapsed.TotalMilliseconds);

				String[] ranked = RankedFileNames(hits);

				recallAt1 += HitsWithin(ranked, labelled.Relevant, 1);
				recallAt3 += HitsWithin(ranked, labelled.Relevant, 3);
				recallAt5 += HitsWithin(ranked, labelled.Relevant, 5);
				reciprocal += ReciprocalRank(ranked, labelled.Relevant);
			}

			Double n = queries.Length;

			return new ProfileAccuracy(
				Profile: profileName,
				Dimension: vectorizer.Descriptor.Dimension,
				RecallAt1: recallAt1 / n,
				RecallAt3: recallAt3 / n,
				RecallAt5: recallAt5 / n,
				MeanReciprocalRank: reciprocal / n,
				IndexMs: indexMs,
				MeanQueryMs: latencies.Average(),
				DatabaseBytes: new FileInfo(databasePath).Length);
		}
		finally
		{
			Dispose(vectorizer);
		}
	}

	// ── 2. the generated corpus: the fitted embedder, and the backend gap as it grows ─────────────

	[Fact]
	[SuppressMessage("Major Code Smell", "S2699:Tests should include assertions",
		Justification = "See the class comment: an instrument, opt-in, asserting nothing on purpose.")]
	public void Measure_Fitted_Accuracy_And_How_The_Backend_Gap_Grows()
	{
		if (!Requested())
		{
			return;
		}

		TopicSeed[] topics = LoadTopics();
		this.Log($"topic seeds: {topics.Length} topics, {topics.Sum(static t => t.Queries.Count)} queries");

		// A curated corpus of twenty documents cannot measure a corpus-fitted embedder fairly: there is
		// almost nothing for a fit to generalise from, and the result would say more about the size of the
		// sample than about the method. The generated corpus exists for this comparison alone.
		List<GeneratedAccuracy> accuracy = [];

		foreach (String profileName in AvailableProfiles(blobOnly: true))
		{
			this.Log($"accuracy on {GeneratedCorpusSize} generated documents: {profileName} ...");
			accuracy.Add(this.MeasureGenerated(profileName, topics));
		}

		foreach (GeneratedAccuracy result in accuracy)
		{
			this.Log($"  {result.Profile,-18} P@1={result.PrecisionAt1:P0} MAP={result.MeanAveragePrecision:F3} " +
				$"nDCG@10={result.Ndcg10:F3} recall@10={result.RecallAt10:P0}");
		}

		// The backend sweep uses the placeholder embedder throughout, and that is deliberate rather than a
		// shortcut: how long a k-NN read takes is a property of the store, not of what produced the numbers
		// in it, and using the fast embedder is what makes three corpus sizes affordable at all.
		List<BackendScale> scaling = [];

		foreach (Int32 size in ScaleSizes)
		{
			scaling.Add(MeasureBackend("programmable-blob", topics, size));

			if (SqliteVecExtension.IsAvailable)
			{
				scaling.Add(MeasureBackend("programmable-vec", topics, size));
			}
		}

		foreach (BackendScale row in scaling)
		{
			this.Log($"  {row.Documents,6} docs  {row.Profile,-18} index={row.IndexMs / 1000.0:F1} s  " +
				$"p50={row.QueryP50Ms:F1} ms  p95={row.QueryP95Ms:F1} ms");
		}

		this.ReportGenerated(accuracy, scaling);
	}

	private GeneratedAccuracy MeasureGenerated(String profileName, TopicSeed[] topics)
	{
		ModuleSet profile = CompositionProfiles.Resolve(profileName);

		using TempFolder folder = new();
		IReadOnlyDictionary<String, String> documentTopics = WriteGeneratedCorpus(folder, topics, GeneratedCorpusSize);

		IndexingConfig config = profileName.StartsWith("lsa", StringComparison.OrdinalIgnoreCase)
			? new IndexingConfig { VectorDimension = LsaDimension }
			: new IndexingConfig();

		IVectorizer vectorizer = profile.CreateVectorizer(config);

		try
		{
			String databasePath = Bootstrap(folder);

			new FolderIndexingPipeline(vectorizer, profile.CreateVectorStoreWriter(), profile.CreateVectorStoreReader())
				.Run(folder.Path, databasePath, config);

			IRetrievalQuery query = profile.CreateRetrievalQuery(
				vectorizer, profile.CreateVectorStoreReader(), null);

			Double precisionAt1 = 0;
			Double meanAveragePrecision = 0;
			Double ndcg = 0;
			Double recall = 0;
			Int32 asked = 0;

			foreach (TopicSeed topic in topics)
			{
				HashSet<String> relevant = documentTopics
					.Where(pair => pair.Value == topic.Id)
					.Select(static pair => pair.Key)
					.ToHashSet(StringComparer.OrdinalIgnoreCase);

				if (relevant.Count == 0)
				{
					continue;
				}

				foreach (String text in topic.Queries)
				{
					String[] ranked = RankedFileNames(query.Search(databasePath, text, new RetrievalOptions(TopK: 10)));

					precisionAt1 += HitsWithin(ranked, relevant, 1);
					meanAveragePrecision += AveragePrecision(ranked, relevant);
					ndcg += NormalisedDiscountedGain(ranked, relevant, 10);
					recall += ranked.Take(10).Count(relevant.Contains) / (Double)Math.Min(relevant.Count, 10);
					asked++;
				}
			}

			Double n = Math.Max(asked, 1);

			return new GeneratedAccuracy(
				Profile: profileName,
				Dimension: vectorizer.Descriptor.Dimension,
				Documents: documentTopics.Count,
				PrecisionAt1: precisionAt1 / n,
				MeanAveragePrecision: meanAveragePrecision / n,
				Ndcg10: ndcg / n,
				RecallAt10: recall / n);
		}
		finally
		{
			Dispose(vectorizer);
		}
	}

	private static BackendScale MeasureBackend(String profileName, TopicSeed[] topics, Int32 documents)
	{
		ModuleSet profile = CompositionProfiles.Resolve(profileName);

		using TempFolder folder = new();
		WriteGeneratedCorpus(folder, topics, documents);

		IndexingConfig config = new();
		IVectorizer vectorizer = profile.CreateVectorizer(config);

		try
		{
			String databasePath = Bootstrap(folder);

			Stopwatch watch = Stopwatch.StartNew();
			new FolderIndexingPipeline(vectorizer, profile.CreateVectorStoreWriter(), profile.CreateVectorStoreReader())
				.Run(folder.Path, databasePath, config);
			Int64 indexMs = watch.ElapsedMilliseconds;

			IRetrievalQuery query = profile.CreateRetrievalQuery(
				vectorizer, profile.CreateVectorStoreReader(), null);

			String[] probes = [.. topics.SelectMany(static topic => topic.Queries)];
			List<Double> latencies = [];

			// One untimed pass first. The first query of a run pays for opening the database, loading the
			// native extension and warming the page cache, and folding that into a percentile would make the
			// backend with the larger fixed cost look slower per query than it is.
			_ = query.Search(databasePath, probes[0], new RetrievalOptions(TopK: 10));

			foreach (String probe in probes)
			{
				watch.Restart();
				_ = query.Search(databasePath, probe, new RetrievalOptions(TopK: 10));
				latencies.Add(watch.Elapsed.TotalMilliseconds);
			}

			latencies.Sort();

			return new BackendScale(
				Profile: profileName,
				Documents: documents,
				IndexMs: indexMs,
				QueryP50Ms: Percentile(latencies, 50),
				QueryP95Ms: Percentile(latencies, 95),
				DatabaseBytes: new FileInfo(databasePath).Length);
		}
		finally
		{
			Dispose(vectorizer);
		}
	}

	// ── 3. cold start: what the first run of a fresh folder actually spends its time on ───────────

	[Fact]
	[SuppressMessage("Major Code Smell", "S2699:Tests should include assertions",
		Justification = "See the class comment: an instrument, opt-in, asserting nothing on purpose.")]
	public void Measure_The_Cold_Start_Of_A_Fresh_Folder()
	{
		if (!Requested())
		{
			return;
		}

		String[] documents = Directory.GetFiles(CorpusDirectory(), "*.txt");
		List<ColdStart> stages = [];

		foreach (String profileName in AvailableProfiles(blobOnly: true))
		{
			ModuleSet profile = CompositionProfiles.Resolve(profileName);

			using TempFolder folder = new();
			foreach (String document in documents)
			{
				File.Copy(document, folder.Combine(Path.GetFileName(document)));
			}

			IndexingConfig config = new();
			IVectorizer vectorizer = profile.CreateVectorizer(config);

			try
			{
				Stopwatch watch = Stopwatch.StartNew();
				String databasePath = Bootstrap(folder);
				Int64 bootstrapMs = watch.ElapsedMilliseconds;

				watch.Restart();
				IndexingResult result = new FolderIndexingPipeline(
						vectorizer, profile.CreateVectorStoreWriter(), profile.CreateVectorStoreReader())
					.Run(folder.Path, databasePath, config);
				Int64 indexMs = watch.ElapsedMilliseconds;

				stages.Add(new ColdStart(profileName, bootstrapMs, indexMs, result.ChunksIndexed));

				this.Log($"  {profileName,-18} database={bootstrapMs} ms  first index={indexMs} ms  " +
					$"({result.ChunksIndexed} chunks)");
			}
			finally
			{
				Dispose(vectorizer);
			}
		}

		this.ReportColdStart(stages, documents.Length);
	}

	// ── 3b. what the embed window actually buys ───────────────────────────────────────────────────

	/// <summary>Embed windows compared, smallest first. One reproduces a call per chunk.</summary>
	private static readonly Int32[] EmbedWindows = [1, 8, 64];

	/// <summary>How many times each window is measured. Passes alternate, so machine drift hits every window.</summary>
	private const Int32 EmbedWindowPasses = 3;

	/// <summary>
	/// Whether gathering chunks into one call is worth anything, measured rather than assumed.
	///
	/// <para>
	/// The comparison is between windows on one tree, in one directory, from one binary — not between two
	/// commits — because the window is a configuration value and a call per chunk is exactly what the
	/// smallest one reproduces. That removes every confound except the machine itself, and the passes
	/// alternate so drift lands on each window rather than on whichever ran last.
	/// </para>
	///
	/// <para>
	/// It runs only against a reachable server. An in-process embedder has no round trip to save, so
	/// measuring it here would produce a row that looks like a result and is arithmetic on noise.
	/// </para>
	/// </summary>
	[Fact]
	[SuppressMessage("Major Code Smell", "S2699:Tests should include assertions",
		Justification = "See the class comment: an instrument, opt-in, asserting nothing on purpose.")]
	public void Measure_What_The_Embed_Window_Buys()
	{
		if (!Requested())
		{
			return;
		}

		if (!OllamaIsReachable())
		{
			this.Log("skipped: the window is a round-trip lever, and there is no server to make round trips to.");

			return;
		}

		String[] documents = Directory.GetFiles(CorpusDirectory(), "*.txt");
		Dictionary<Int32, List<Int64>> timings = EmbedWindows.ToDictionary(static w => w, static _ => new List<Int64>());

		for (Int32 pass = 0; pass < EmbedWindowPasses; pass++)
		{
			foreach (Int32 window in EmbedWindows)
			{
				Int64 elapsed = IndexOnceWithWindow(documents, window);
				timings[window].Add(elapsed);
				this.Log($"  pass {pass + 1}, window {window,3}: {elapsed} ms");
			}
		}

		List<EmbedWindowResult> results = [.. EmbedWindows.Select(window => new EmbedWindowResult(
			Window: window,
			Timings: timings[window],
			MedianMs: Median(timings[window])))];

		Double slowest = results[0].MedianMs;

		foreach (EmbedWindowResult result in results)
		{
			this.Log($"  window {result.Window,3}: median {result.MedianMs:F0} ms " +
				$"({slowest / Math.Max(result.MedianMs, 1):F2}x against a call per chunk)");
		}

		this.ReportEmbedWindow(results, documents.Length);
	}

	private static Int64 IndexOnceWithWindow(String[] documents, Int32 window)
	{
		ModuleSet profile = CompositionProfiles.Resolve("ollama-blob");

		using TempFolder folder = new();
		foreach (String document in documents)
		{
			File.Copy(document, folder.Combine(Path.GetFileName(document)));
		}

		IndexingConfig config = new() { EmbeddingBatchSizeChunks = window };
		IVectorizer vectorizer = profile.CreateVectorizer(config);

		try
		{
			String databasePath = Bootstrap(folder);

			Stopwatch watch = Stopwatch.StartNew();
			new FolderIndexingPipeline(vectorizer, profile.CreateVectorStoreWriter(), profile.CreateVectorStoreReader())
				.Run(folder.Path, databasePath, config);

			return watch.ElapsedMilliseconds;
		}
		finally
		{
			Dispose(vectorizer);
		}
	}

	/// <summary>
	/// The middle value, not the mean. One pass that lands while something else on the machine is busy
	/// would drag a mean far enough to invent a difference between two windows.
	/// </summary>
	private static Double Median(List<Int64> values)
	{
		if (values.Count == 0)
		{
			return 0;
		}

		List<Int64> sorted = [.. values.Order()];
		Int32 middle = sorted.Count / 2;

		if (sorted.Count % 2 == 1)
		{
			return sorted[middle];
		}

		return (sorted[middle - 1] + sorted[middle]) / 2.0;
	}

	private void ReportEmbedWindow(List<EmbedWindowResult> results, Int32 documents)
	{
		StringBuilder builder = Header(
			"What the embed window buys",
			$"The {documents}-document curated corpus indexed from nothing at each window, against a local "
			+ "embedding server. One window size means a call per chunk, which is what the pipeline did before "
			+ $"it gathered chunks across files. {EmbedWindowPasses} passes, alternating between windows so that "
			+ "machine drift lands on all of them rather than on whichever ran last.");

		builder.AppendLine("| Window | Median (ms) | Passes (ms) | Against a call per chunk |");
		builder.AppendLine("|---:|---:|---|---:|");

		Double slowest = results.Count == 0 ? 0 : results[0].MedianMs;

		foreach (EmbedWindowResult result in results)
		{
			builder.AppendLine(CultureInfo.InvariantCulture,
				$"| {result.Window} | {result.MedianMs:F0} | {String.Join(", ", result.Timings)} | "
				+ $"{slowest / Math.Max(result.MedianMs, 1):F2}x |");
		}

		builder.AppendLine();
		builder.AppendLine("_The spread across passes is the thing to read before the ratio: where the passes overlap, "
			+ "the medians are not separated by this many samples._");

		this.Write("embed-window.md", builder);
	}

	// ── 4. what the native backend costs on disk ──────────────────────────────────────────────────

	[Fact]
	[SuppressMessage("Major Code Smell", "S2699:Tests should include assertions",
		Justification = "See the class comment: an instrument, opt-in, asserting nothing on purpose.")]
	public void Diagnose_What_Each_Vector_Backend_Costs_On_Disk()
	{
		if (!Requested())
		{
			return;
		}

		if (!SqliteVecExtension.IsAvailable)
		{
			this.Log("skipped: the sqlite-vec native extension is not available on this platform.");

			return;
		}

		TopicSeed[] topics = LoadTopics();

		foreach (Int32 size in ScaleSizes)
		{
			BackendScale blob = MeasureBackend("programmable-blob", topics, size);
			BackendScale vec = MeasureBackend("programmable-vec", topics, size);

			this.Log($"  {size,6} docs  blob={blob.DatabaseBytes / 1024} KB  vec={vec.DatabaseBytes / 1024} KB  " +
				$"ratio={vec.DatabaseBytes / (Double)blob.DatabaseBytes:F2}x");
		}
	}

	// ── corpus ────────────────────────────────────────────────────────────────────────────────────

	/// <summary>
	/// Builds <paramref name="count"/> documents from the topic seeds, and returns which topic each one
	/// belongs to so a query's relevant set can be derived rather than hand-listed.
	///
	/// <para>
	/// Each document takes a rotating pair of its topic's variants, so two documents on one topic overlap in
	/// meaning and barely at all in wording. Filler is appended from a fixed neutral vocabulary to give the
	/// tokenizer something to chew on without adding a second subject — the filler is identical across
	/// topics precisely so that it cannot help anything rank.
	/// </para>
	/// </summary>
	private static IReadOnlyDictionary<String, String> WriteGeneratedCorpus(
		TempFolder folder,
		TopicSeed[] topics,
		Int32 count)
	{
		Dictionary<String, String> documentTopics = new(StringComparer.OrdinalIgnoreCase);

		for (Int32 i = 0; i < count; i++)
		{
			TopicSeed topic = topics[i % topics.Length];
			Int32 round = i / topics.Length;

			String first = topic.Variants[round % topic.Variants.Count];
			String second = topic.Variants[(round + 1 + (round / topic.Variants.Count)) % topic.Variants.Count];

			StringBuilder text = new();
			text.Append(first).Append(' ').Append(second);

			for (Int32 f = 0; f < 12; f++)
			{
				text.Append(' ').Append(Filler[(i + f) % Filler.Length]);
			}

			String name = $"{topic.Id}-{round:D4}.txt";
			File.WriteAllText(folder.Combine(name), text.ToString());
			documentTopics[name] = topic.Id;
		}

		return documentTopics;
	}

	/// <summary>
	/// Neutral words shared by every generated document, so they carry no signal about which topic a
	/// document belongs to and cannot flatter any embedder.
	/// </summary>
	private static readonly String[] Filler =
	[
		"note", "entry", "record", "section", "summary", "detail", "reference", "item",
		"page", "extract", "passage", "listing", "figure", "appendix", "remark", "caption",
	];

	private static String CorpusDirectory() => Path.Combine(SourceDirectory(), "BenchmarkCorpus");

	private static LabelledQuery[] LoadLabelledQueries()
	{
		List<LabelledQuery> queries = [];

		foreach (String line in File.ReadAllLines(Path.Combine(SourceDirectory(), "BenchmarkCorpus.queries.tsv")))
		{
			if (String.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
			{
				continue;
			}

			String[] parts = line.Split('\t', StringSplitOptions.TrimEntries);

			queries.Add(new LabelledQuery(
				parts[0],
				parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
					.ToHashSet(StringComparer.OrdinalIgnoreCase)));
		}

		return [.. queries];
	}

	private static TopicSeed[] LoadTopics()
	{
		List<TopicSeed> topics = [];
		String? id = null;
		List<String> variants = [];
		List<String> queries = [];

		void Flush()
		{
			if (id is not null)
			{
				topics.Add(new TopicSeed(id, [.. variants], [.. queries]));
			}

			variants.Clear();
			queries.Clear();
		}

		foreach (String line in File.ReadAllLines(Path.Combine(SourceDirectory(), "BenchmarkCorpus.topics.txt")))
		{
			if (String.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
			{
				continue;
			}

			if (line.StartsWith("T ", StringComparison.Ordinal))
			{
				Flush();
				id = line[2..].Trim();
			}
			else if (line.StartsWith("V ", StringComparison.Ordinal))
			{
				variants.Add(line[2..].Trim());
			}
			else if (line.StartsWith("Q ", StringComparison.Ordinal))
			{
				queries.Add(line[2..].Trim());
			}
		}

		Flush();

		return [.. topics];
	}

	// ── metrics ───────────────────────────────────────────────────────────────────────────────────

	/// <summary>
	/// The ranked distinct source files behind a hit list. Hits are chunks, and one document can occupy
	/// several of the top places; collapsing to first appearance is what makes a rank a rank about
	/// documents, which is what every metric below is defined over.
	/// </summary>
	private static String[] RankedFileNames(IReadOnlyList<RetrievalHit> hits)
	{
		List<String> ranked = [];

		foreach (RetrievalHit hit in hits)
		{
			String name = Path.GetFileName(hit.FilePath);

			if (!ranked.Contains(name, StringComparer.OrdinalIgnoreCase))
			{
				ranked.Add(name);
			}
		}

		return [.. ranked];
	}

	private static Double HitsWithin(String[] ranked, IReadOnlyCollection<String> relevant, Int32 k)
		=> ranked.Take(k).Any(relevant.Contains) ? 1 : 0;

	private static Double ReciprocalRank(String[] ranked, IReadOnlyCollection<String> relevant)
	{
		for (Int32 i = 0; i < ranked.Length; i++)
		{
			if (relevant.Contains(ranked[i]))
			{
				return 1.0 / (i + 1);
			}
		}

		return 0;
	}

	private static Double AveragePrecision(String[] ranked, IReadOnlyCollection<String> relevant)
	{
		Int32 found = 0;
		Double sum = 0;

		for (Int32 i = 0; i < ranked.Length; i++)
		{
			if (relevant.Contains(ranked[i]))
			{
				found++;
				sum += found / (Double)(i + 1);
			}
		}

		return found == 0 ? 0 : sum / Math.Min(relevant.Count, ranked.Length);
	}

	private static Double NormalisedDiscountedGain(String[] ranked, IReadOnlyCollection<String> relevant, Int32 k)
	{
		Double gain = 0;

		for (Int32 i = 0; i < Math.Min(k, ranked.Length); i++)
		{
			if (relevant.Contains(ranked[i]))
			{
				gain += 1.0 / Math.Log2(i + 2);
			}
		}

		Double ideal = 0;

		for (Int32 i = 0; i < Math.Min(k, relevant.Count); i++)
		{
			ideal += 1.0 / Math.Log2(i + 2);
		}

		return ideal <= 0 ? 0 : gain / ideal;
	}

	private static Double Percentile(IReadOnlyList<Double> sortedAscending, Double percentile)
	{
		if (sortedAscending.Count == 0)
		{
			return 0;
		}

		Int32 index = (Int32)Math.Ceiling(percentile / 100.0 * sortedAscending.Count) - 1;

		return sortedAscending[Math.Clamp(index, 0, sortedAscending.Count - 1)];
	}

	// ── composition ───────────────────────────────────────────────────────────────────────────────

	/// <summary>
	/// The profiles worth running here, given what this machine can actually do.
	///
	/// <para>
	/// A profile naming Ollama composes everywhere — reaching the server is a runtime condition, not a
	/// platform one — so whether it is included is decided by probing, not by <c>IsAvailable</c>. Running it
	/// against an absent server would produce a row of failures that look like a result.
	/// </para>
	/// </summary>
	private static IEnumerable<String> AvailableProfiles(Boolean blobOnly = false)
	{
		Boolean ollama = OllamaIsReachable();
		Boolean vec = SqliteVecExtension.IsAvailable;

		// The fitted profiles are in-process and always run; the default is one of them, so a table
		// without them would not carry the number the default is chosen on.
		yield return "programmable-blob";
		yield return "lsa-blob";

		if (ollama)
		{
			yield return "ollama-blob";
		}

		if (blobOnly)
		{
			yield break;
		}

		if (vec)
		{
			yield return "programmable-vec";
			yield return "lsa-vec";
		}

		if (vec && ollama)
		{
			yield return "ollama-vec";
		}
	}

	private static Boolean OllamaIsReachable()
	{
		IndexingConfig config = new();

		using OllamaEmbeddingVectorizer vectorizer = new(
			config.OllamaEndpoint, config.OllamaModel, config.OllamaModelVersionId, config.OllamaEmbeddingDimension,
			TimeSpan.FromSeconds(config.OllamaTimeoutSeconds));

		try
		{
			using CancellationTokenSource probe = new(TimeSpan.FromSeconds(30));
			_ = vectorizer.VectorizeAsync(["probe"], EmbeddingKind.Document, probe.Token).AsTask().GetAwaiter().GetResult();

			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static String Bootstrap(TempFolder folder)
		=> new FolderDatabaseBootstrapper().EnsureInitialized(folder.Path, new PersistenceConfig()).DatabasePath;

	private static void Dispose(IVectorizer vectorizer)
	{
		if (vectorizer is IDisposable disposable)
		{
			disposable.Dispose();
		}
	}

	private static Boolean Requested()
		=> !String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(GateVariable));

	private static String SourceDirectory([CallerFilePath] String path = "") => Path.GetDirectoryName(path)!;

	private static String BenchmarksDirectory()
	{
		String directory = Path.GetFullPath(Path.Combine(SourceDirectory(), "..", "docs", "benchmarks"));
		Directory.CreateDirectory(directory);

		return directory;
	}

	private void Log(String message) => this._output.WriteLine(message);

	// ── reports ───────────────────────────────────────────────────────────────────────────────────

	private void Write(String fileName, StringBuilder content)
	{
		String path = Path.Combine(BenchmarksDirectory(), fileName);

		// Written with a bare newline rather than the platform's. These reports are committed documents, and
		// a file whose line endings depend on which machine regenerated it shows up as changed when nothing
		// about the measurement has changed.
		File.WriteAllText(path, content.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
		this.Log($"wrote {path}");
	}

	private static StringBuilder Header(String title, String summary)
	{
		StringBuilder builder = new();
		builder.AppendLine(CultureInfo.InvariantCulture, $"# {title}");
		builder.AppendLine();
		builder.AppendLine(CultureInfo.InvariantCulture, $"_Measured {DateTime.Now:yyyy-MM-dd} on this machine. " +
			$"Regenerate with `{GateVariable}=1 dotnet test --filter FullyQualifiedName~SemanticSearchBenchmark`._");
		builder.AppendLine();
		builder.AppendLine(summary);
		builder.AppendLine();

		return builder;
	}

	private void ReportCurated(List<ProfileAccuracy> results, Int32 documents, Int32 queries)
	{
		StringBuilder builder = Header(
			"Semantic search — curated corpus",
			$"{documents} hand-written documents, {queries} hand-labelled queries. Each query is worded to avoid the "
			+ "distinctive vocabulary of the document it should find, so ranking it correctly takes meaning rather "
			+ "than shared words.");

		builder.AppendLine("| Profile | dim | Recall@1 | Recall@3 | Recall@5 | MRR@10 | Index (ms) | Mean query (ms) | DB |");
		builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");

		foreach (ProfileAccuracy r in results)
		{
			builder.AppendLine(CultureInfo.InvariantCulture,
				$"| `{r.Profile}` | {r.Dimension} | {r.RecallAt1:P0} | {r.RecallAt3:P0} | {r.RecallAt5:P0} | "
				+ $"{r.MeanReciprocalRank:F3} | {r.IndexMs} | {r.MeanQueryMs:F1} | {r.DatabaseBytes / 1024} KB |");
		}

		builder.AppendLine();
		builder.AppendLine("_Recall@k is the share of queries whose correct document appears in the top k. MRR@10 is the "
			+ "mean reciprocal rank of the first correct document._");

		this.Write("semantic-search-results.md", builder);
	}

	private void ReportGenerated(List<GeneratedAccuracy> accuracy, List<BackendScale> scaling)
	{
		StringBuilder builder = Header(
			"Semantic search — generated corpus and backend scaling",
			$"{GeneratedCorpusSize} documents built from the topic seeds, several per topic, each pairing differently "
			+ "worded variants of the same subject. Every document of a topic counts as relevant to that topic's "
			+ "queries, which is what makes the multi-relevant metrics below meaningful.");

		builder.AppendLine("| Profile | dim | P@1 | MAP | nDCG@10 | Recall@10 |");
		builder.AppendLine("|---|---:|---:|---:|---:|---:|");

		foreach (GeneratedAccuracy r in accuracy)
		{
			builder.AppendLine(CultureInfo.InvariantCulture,
				$"| `{r.Profile}` | {r.Dimension} | {r.PrecisionAt1:P0} | {r.MeanAveragePrecision:F3} | "
				+ $"{r.Ndcg10:F3} | {r.RecallAt10:P0} |");
		}

		builder.AppendLine();
		builder.AppendLine("_MAP is the headline number where a query has many relevant documents._");
		builder.AppendLine();
		builder.AppendLine("## Backend latency as the corpus grows");
		builder.AppendLine();
		builder.AppendLine("Same embedder throughout — how long a k-NN read takes is a property of the store, not of "
			+ "what produced the numbers in it.");
		builder.AppendLine();
		builder.AppendLine("| Docs | Profile | Index (s) | Query p50 (ms) | Query p95 (ms) | DB |");
		builder.AppendLine("|---:|---|---:|---:|---:|---:|");

		foreach (BackendScale r in scaling)
		{
			builder.AppendLine(CultureInfo.InvariantCulture,
				$"| {r.Documents} | `{r.Profile}` | {r.IndexMs / 1000.0:F1} | {r.QueryP50Ms:F1} | {r.QueryP95Ms:F1} | "
				+ $"{r.DatabaseBytes / 1024} KB |");
		}

		this.Write("semantic-search-full-results.md", builder);
	}

	private void ReportColdStart(List<ColdStart> stages, Int32 documents)
	{
		StringBuilder builder = Header(
			"Cold start — a folder indexed for the first time",
			$"The {documents}-document curated corpus, from nothing: creating the database, then the first full index. "
			+ "Split because the two are charged to different things — one is schema creation, the other is almost "
			+ "entirely embedding.");

		builder.AppendLine("| Profile | Database (ms) | First index (ms) | Chunks | Per chunk (ms) |");
		builder.AppendLine("|---|---:|---:|---:|---:|");

		foreach (ColdStart s in stages)
		{
			builder.AppendLine(CultureInfo.InvariantCulture,
				$"| `{s.Profile}` | {s.BootstrapMs} | {s.IndexMs} | {s.Chunks} | "
				+ $"{(s.Chunks == 0 ? 0 : s.IndexMs / (Double)s.Chunks):F1} |");
		}

		this.Write("cold-start-indexing.md", builder);
	}

	// ── shapes ────────────────────────────────────────────────────────────────────────────────────

	private sealed record LabelledQuery(String Text, IReadOnlyCollection<String> Relevant);

	private sealed record TopicSeed(String Id, IReadOnlyList<String> Variants, IReadOnlyList<String> Queries);

	private sealed record ProfileAccuracy(
		String Profile,
		Int32 Dimension,
		Double RecallAt1,
		Double RecallAt3,
		Double RecallAt5,
		Double MeanReciprocalRank,
		Int64 IndexMs,
		Double MeanQueryMs,
		Int64 DatabaseBytes);

	private sealed record GeneratedAccuracy(
		String Profile,
		Int32 Dimension,
		Int32 Documents,
		Double PrecisionAt1,
		Double MeanAveragePrecision,
		Double Ndcg10,
		Double RecallAt10);

	private sealed record BackendScale(
		String Profile,
		Int32 Documents,
		Int64 IndexMs,
		Double QueryP50Ms,
		Double QueryP95Ms,
		Int64 DatabaseBytes);

	private sealed record EmbedWindowResult(Int32 Window, IReadOnlyList<Int64> Timings, Double MedianMs);

	private sealed record ColdStart(String Profile, Int64 BootstrapMs, Int64 IndexMs, Int32 Chunks);
}
