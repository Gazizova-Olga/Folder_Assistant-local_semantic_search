using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using FolderAssistant.Embedding;
using FolderAssistant.Extraction;
using FolderAssistant.Indexing;
using FolderAssistant.Persistence;
using FolderAssistant.Retrieval;
using FolderAssistant.Tools;
using Xunit.Abstractions;

namespace FolderAssistant.Tests;

/// <summary>
/// Semantic search over a real folder, asked in three languages, through the tool the agent calls.
///
/// <para>
/// This is a measuring instrument, not a test: it is gated on <c>CORPUS_PROBE</c> naming a folder and
/// returns immediately without it, asserting nothing, like the other benchmarks here. What it adds
/// over <c>SemanticSearchBenchmark</c> is the path: the queries go through <see cref="SearchTools"/>,
/// so the over-fetch, the passage rebuild and its hash verification, the low-confidence screen, the
/// score-gap cutoff and the budget reduction are all in the measurement — the benchmark measures the
/// ranking underneath them, which is a different number and a weaker claim about what a question gets.
/// </para>
///
/// <para>
/// The corpus it expects carries a <c>queries.tsv</c> in the repository's own labelled format and a
/// <c>corpus-manifest.meta</c> naming, per file, the token count and the chunk count its sizes were
/// designed for. Both are read: the first scores retrieval, the second checks that the chunker
/// produced the chunks the corpus was built to produce, which is the one way to tell a corpus that
/// lands on chunk boundaries from one that was meant to.
/// </para>
///
/// <example>
/// <code>
/// CORPUS_PROBE=C:\Users\me\Documents\assistant-test-corpus dotnet test \
///   --filter "FullyQualifiedName~MultilingualCorpusProbe" -l "console;verbosity=detailed"
/// </code>
/// </example>
/// </summary>
public sealed class MultilingualCorpusProbe
{
	private const String GateVariable = "CORPUS_PROBE";
	private const String ProfileVariable = "CORPUS_PROBE_PROFILE";
	private const String BatchVariable = "CORPUS_PROBE_BATCH";
	private const String TimeoutVariable = "CORPUS_PROBE_TIMEOUT";
	private const String DatabaseVariable = "CORPUS_PROBE_DB";

	/// <summary>
	/// Smaller than the shipped embed window, and with a deadline five times the shipped one, because
	/// the shipped defaults are sized for English on a machine that had a faster embedder. Measured
	/// here on 2026-09-24, CPU-only (<c>ollama ps</c> reports 100% CPU), on 256-token chunks of this
	/// corpus: about 2.1 s for an English chunk and 4.3 s for a Russian one, 3.0 s per chunk over a
	/// mixed batch of sixteen. A window of 64 mixed chunks is therefore about 190 s of work against a
	/// 120 s deadline, which the deadline correctly ends — and an instrument that spent its run
	/// rediscovering that would never reach the thing it exists to measure. Both are overridable.
	/// </summary>
	private const Int32 DefaultBatchChunks = 16;

	private const Int32 DefaultTimeoutSeconds = 600;

	/// <summary>What a model asks for by default, and therefore what the cutoff and the budget see.</summary>
	private const Int32 PassagesPerQuery = 5;

	private readonly ITestOutputHelper _output;

	public MultilingualCorpusProbe(ITestOutputHelper output)
	{
		this._output = output;
	}

	[Fact]
	public void Index_And_Ask_The_Corpus_In_Three_Languages()
	{
		String? corpus = Environment.GetEnvironmentVariable(GateVariable);

		if (String.IsNullOrWhiteSpace(corpus))
		{
			return;
		}

		Directory.Exists(corpus).Should().BeTrue($"{GateVariable} names a folder to index");

		String profileName = Environment.GetEnvironmentVariable(ProfileVariable) is { Length: > 0 } named
			? named
			: CompositionProfiles.ResolveDefault().Profile.Name;

		ModuleSet profile = CompositionProfiles.Resolve(profileName);

		IndexingConfig config = new()
		{
			EmbeddingBatchSizeChunks = Setting(BatchVariable, DefaultBatchChunks),
			OllamaTimeoutSeconds = Setting(TimeoutVariable, DefaultTimeoutSeconds),
		};

		IVectorizer vectorizer = profile.CreateVectorizer(config);

		// Where the index lives. A temporary folder by default, thrown away with the run; a folder named
		// by CORPUS_PROBE_DB when the questions are what is being worked on rather than the indexing.
		// Embedding this corpus costs about three quarters of an hour of CPU, and the pass skips a file
		// whose content hash matches and whose vectors the active model already holds — so a kept
		// database turns re-scoring into a minute. It is the same database the application would build.
		String? keptDatabase = Environment.GetEnvironmentVariable(DatabaseVariable);
		using TempFolder temporary = new();
		String databaseFolder = String.IsNullOrWhiteSpace(keptDatabase) ? temporary.Path : keptDatabase;
		Directory.CreateDirectory(databaseFolder);

		try
		{
			String databasePath = new FolderDatabaseBootstrapper()
				.EnsureInitialized(databaseFolder, new PersistenceConfig()).DatabasePath;

			this._output.WriteLine($"corpus   : {corpus}");
			this._output.WriteLine($"profile  : {profileName} (dim {vectorizer.Descriptor.Dimension}, {vectorizer.Descriptor.ModelName})");
			this._output.WriteLine($"window   : {config.EmbeddingBatchSizeChunks} chunks per embed call, {config.OllamaTimeoutSeconds} s deadline");
			this._output.WriteLine($"index    : {(keptDatabase is null ? "temporary, discarded with the run" : databaseFolder)}");

			Stopwatch watch = Stopwatch.StartNew();
			IndexingResult indexed = new FolderIndexingPipeline(
				vectorizer, profile.CreateVectorStoreWriter(), profile.CreateVectorStoreReader())
				.Run(corpus, databasePath, config);
			Int64 indexMs = watch.ElapsedMilliseconds;

			this._output.WriteLine(
				$"indexed  : {indexed.FilesIndexed} files, {indexed.ChunksIndexed} chunks, {indexed.VectorsIndexed} vectors in {indexMs} ms");

			this.ReportChunkShape(corpus, indexed);

			SearchTools tools = new(
				profile.CreateRetrievalQuery(vectorizer, profile.CreateVectorStoreReader(), null),
				databasePath,
				new PassageBuilder(corpus, TextExtractorRegistry.Default),
				new TokenBudgetContextReducer());

			this.Ask(tools, LoadQueries(corpus));
		}
		finally
		{
			if (vectorizer is IDisposable disposable)
			{
				disposable.Dispose();
			}
		}
	}

	/// <summary>
	/// Did the chunker produce what the corpus was sized for? A corpus whose files were built to end
	/// on a chunk boundary is only worth what that claim is worth, and the claim is the generator's
	/// arithmetic about a rule that lives in this repository — so it is checked against the rule
	/// rather than trusted. A mismatch is reported, not thrown: the retrieval numbers below are still
	/// real, they just stop being numbers about the shape the corpus claims.
	/// </summary>
	private void ReportChunkShape(String corpus, IndexingResult indexed)
	{
		String manifestPath = Path.Combine(corpus, "corpus-manifest.meta");

		if (!File.Exists(manifestPath))
		{
			this._output.WriteLine("manifest : none — chunk shape not checked");
			return;
		}

		using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
		JsonElement root = manifest.RootElement;

		Int32 expectedChunks = root.GetProperty("totalChunks").GetInt32();
		Int32 exact = root.GetProperty("exactChunkFiles").GetInt32();
		Int32 partial = root.GetProperty("partialChunkFiles").GetInt32();

		this._output.WriteLine(
			$"shape    : {exact} files end on a full chunk, {partial} leave a short one; "
			+ $"designed for {expectedChunks} chunks, indexed {indexed.ChunksIndexed}");

		if (indexed.ChunksIndexed != expectedChunks)
		{
			this._output.WriteLine(
				"           ^ the corpus was generated against a different chunk size or step than this tree uses, "
				+ "or it holds files the generator did not label (edge cases are expected to differ)");
		}
	}

	private void Ask(SearchTools tools, IReadOnlyList<LabelledQuery> queries)
	{
		if (queries.Count == 0)
		{
			this._output.WriteLine("queries  : none — expected queries.tsv beside the corpus");
			return;
		}

		Dictionary<String, Score> byLanguage = new(StringComparer.Ordinal);
		Score overall = new();
		List<Double> latencies = [];
		List<String> misses = [];

		foreach (LabelledQuery labelled in queries)
		{
			Stopwatch watch = Stopwatch.StartNew();
			SemanticSearchResult result = tools.SearchIndex(labelled.Text, PassagesPerQuery);
			latencies.Add(watch.Elapsed.TotalMilliseconds);

			String[] ranked = [.. result.Passages
				.Select(passage => Path.GetFileName(passage.Path))
				.Distinct(StringComparer.OrdinalIgnoreCase)];

			Int32 rank = Rank(ranked, labelled.Relevant);

			overall.Add(rank, result.Passages.Count);
			byLanguage.TryAdd(labelled.Language, new Score());
			byLanguage[labelled.Language].Add(rank, result.Passages.Count);

			if (rank != 1)
			{
				misses.Add($"    [{labelled.Language}] \"{labelled.Text}\" → "
					+ (ranked.Length == 0
						? $"nothing ({result.Note ?? "no note"})"
						: $"{String.Join(", ", ranked.Take(3))} (wanted one of {String.Join(", ", labelled.Relevant.Take(3))})"));
			}
		}

		latencies.Sort();

		this._output.WriteLine(String.Empty);
		this._output.WriteLine($"queries  : {queries.Count}, asking for {PassagesPerQuery} passages each");
		this._output.WriteLine($"latency  : p50 {latencies[latencies.Count / 2]:F0} ms, p95 {latencies[(Int32)(latencies.Count * 0.95)]:F0} ms");
		this._output.WriteLine(String.Empty);
		this._output.WriteLine("           R@1     R@3     MRR     passages/query");

		foreach ((String language, Score score) in byLanguage.OrderBy(pair => pair.Key, StringComparer.Ordinal))
		{
			this._output.WriteLine($"  {language,-7}  {score.Line()}");
		}

		this._output.WriteLine($"  {"all",-7}  {overall.Line()}");

		if (misses.Count > 0)
		{
			this._output.WriteLine(String.Empty);
			this._output.WriteLine($"not first ({misses.Count}):");

			foreach (String miss in misses)
			{
				this._output.WriteLine(miss);
			}
		}
	}

	/// <summary>
	/// The rank of the first relevant file, or zero for none. Files rather than passages: a query is
	/// answered when the right document comes back, and which of its chunks carried the answer is the
	/// reducer's business.
	/// </summary>
	private static Int32 Rank(String[] ranked, IReadOnlySet<String> relevant)
	{
		for (Int32 i = 0; i < ranked.Length; i++)
		{
			if (relevant.Contains(ranked[i]))
			{
				return i + 1;
			}
		}

		return 0;
	}

	/// <summary>
	/// The labelled file's language, read from the name the generator gives it (<c>topic-lang-…</c>),
	/// so a query is scored in the language it was asked in without a second file to keep in step.
	/// A name that does not carry one is scored as <c>mixed</c>, which is what the cross-lingual
	/// queries — relevant in all three — come out as.
	/// </summary>
	private static String LanguageOf(IEnumerable<String> relevant)
	{
		HashSet<String> languages = new(StringComparer.Ordinal);

		foreach (String file in relevant)
		{
			String[] parts = file.Split('-');
			languages.Add(parts.Length >= 2 ? parts[1] : "?");
		}

		return languages.Count == 1 ? languages.First() : "cross";
	}

	/// <summary>An integer from the environment, or the default. A value that is not one is refused.</summary>
	private static Int32 Setting(String variable, Int32 fallback)
	{
		String? value = Environment.GetEnvironmentVariable(variable);

		if (String.IsNullOrWhiteSpace(value))
		{
			return fallback;
		}

		return Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out Int32 parsed) && parsed > 0
			? parsed
			: throw new InvalidOperationException($"{variable} is '{value}', which is not a positive whole number.");
	}

	private static IReadOnlyList<LabelledQuery> LoadQueries(String corpus)
	{
		String path = Path.Combine(corpus, "queries.tsv");

		if (!File.Exists(path))
		{
			return [];
		}

		List<LabelledQuery> queries = [];

		foreach (String line in File.ReadAllLines(path, Encoding.UTF8))
		{
			if (String.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
			{
				continue;
			}

			String[] parts = line.Split('\t', StringSplitOptions.TrimEntries);

			if (parts.Length < 2)
			{
				continue;
			}

			HashSet<String> relevant = parts[1]
				.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);

			queries.Add(new LabelledQuery(parts[0], relevant, LanguageOf(relevant)));
		}

		return queries;
	}

	private sealed record LabelledQuery(String Text, IReadOnlySet<String> Relevant, String Language);

	private sealed class Score
	{
		private Int32 _asked;
		private Int32 _first;
		private Int32 _withinThree;
		private Double _reciprocal;
		private Int32 _passages;

		public void Add(Int32 rank, Int32 passages)
		{
			this._asked++;
			this._passages += passages;

			if (rank == 0)
			{
				return;
			}

			this._reciprocal += 1.0 / rank;

			if (rank <= 3)
			{
				this._withinThree++;
			}

			if (rank == 1)
			{
				this._first++;
			}
		}

		public String Line()
		{
			Double asked = this._asked;

			return String.Create(
				CultureInfo.InvariantCulture,
				$"{this._first / asked,6:P0}  {this._withinThree / asked,6:P0}  {this._reciprocal / asked,6:F3}  {this._passages / asked,6:F1}   (n={this._asked})");
		}
	}
}
