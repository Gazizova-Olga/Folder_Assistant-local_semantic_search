using System.Diagnostics;
using FolderAssistant.Embedding;
using FolderAssistant.Indexing;
using FolderAssistant.Persistence;
using FolderAssistant.Retrieval;
using Xunit.Abstractions;

namespace FolderAssistant.Tests;

/// <summary>
/// Measures indexing and retrieval against a corpus large enough for the shape of the queries to
/// matter, which the rest of the suite is not: its folders hold three files, and at that size an
/// O(files × vectors) query and a constant-time one are indistinguishable.
///
/// <para>
/// **This asserts nothing.** It is a measuring instrument, and it is opt-in — set
/// <c>BENCHMARK_FILES</c> to a file count to run it. Left unset it returns immediately, because a
/// benchmark that runs on every build is a slow test suite rather than evidence.
/// </para>
///
/// <para>
/// The corpus is generated rather than pointed at a real folder, so a number from one machine can
/// be compared with a number from another. Set <c>BENCHMARK_CORPUS</c> to measure a real folder
/// instead; that is the more honest input and the less repeatable one.
/// </para>
/// </summary>
public sealed class CorpusBenchmark
{
	private readonly ITestOutputHelper _output;

	public CorpusBenchmark(ITestOutputHelper output) => this._output = output;

	[Fact]
	public void Measure_The_Baseline()
	{
		String? fileCountSetting = Environment.GetEnvironmentVariable("BENCHMARK_FILES");
		String? realCorpus = Environment.GetEnvironmentVariable("BENCHMARK_CORPUS");

		if (String.IsNullOrWhiteSpace(fileCountSetting) && String.IsNullOrWhiteSpace(realCorpus))
		{
			return;
		}

		using TempFolder generated = new();
		String corpus = realCorpus ?? generated.Path;

		if (String.IsNullOrWhiteSpace(realCorpus))
		{
			Int32 fileCount = Int32.Parse(fileCountSetting!);
			GenerateCorpus(generated, fileCount);
			this.Log($"generated {fileCount} files");
		}

		IndexingConfig config = new();
		ProgrammableEmbeddingVectorizer vectorizer = new("bench-v1", config.VectorDimension);
		String modelVersionId = vectorizer.Descriptor.ModelVersionId;

		DatabaseBootstrapResult database = new FolderDatabaseBootstrapper()
			.EnsureInitialized(corpus, new PersistenceConfig());

		Stopwatch watch = Stopwatch.StartNew();

		// Counted through the streaming enumerator on purpose: materialising the scan here would hold
		// every file's text alive for the rest of the run and swamp the figures below.
		Int32 scannedCount = new LocalTextFileScanner().Enumerate(corpus, config.MaxTextFileSizeBytes).Count();
		this.Log($"scan: {scannedCount} files in {watch.ElapsedMilliseconds} ms");

		FolderIndexingPipeline pipeline = new(vectorizer);

		// The pipeline streams, so it should retain only metadata and vectors — never file or chunk
		// text. Measure both what the run churns and what it is still holding when it returns; a full
		// collect first, so retention is what the run is actually keeping rather than what the
		// collector has yet to sweep.
		Int64 liveBeforeIndex = LiveBytes();
		Int64 allocatedBeforeIndex = GC.GetTotalAllocatedBytes();

		watch.Restart();
		IndexingResult first = pipeline.Run(corpus, database.DatabasePath, config);
		Double coldSeconds = watch.Elapsed.TotalSeconds;

		Int64 indexChurn = (GC.GetTotalAllocatedBytes() - allocatedBeforeIndex) / 1024 / 1024;
		Int64 indexRetained = (LiveBytes() - liveBeforeIndex) / 1024 / 1024;

		this.Log(
			$"index (cold): {first.FilesIndexed} embedded, {first.ChunksIndexed} chunks, " +
			$"{first.VectorsIndexed} vectors in {coldSeconds:F1} s");
		this.Log($"  retained by the index run (metadata + vectors only): {indexRetained} MB");
		this.Log($"  allocated during the index run (churn): {indexChurn} MB");

		// The pass that matters for steady state. Nothing changed, so nothing is embedded — every
		// millisecond here is the cost of working out that there is nothing to do.
		watch.Restart();
		IReadOnlyDictionary<String, IndexedFileState> manifest =
			new SqliteFolderManifestReader().ReadFileStates(database.DatabasePath, modelVersionId);
		this.Log($"manifest read: {manifest.Count} files in {watch.ElapsedMilliseconds} ms");

		watch.Restart();
		IndexingResult second = pipeline.Run(corpus, database.DatabasePath, config);
		this.Log(
			$"index (warm, nothing to do): {second.FilesUnchanged} unchanged in {watch.Elapsed.TotalSeconds:F1} s");

		// Where does a query's time actually go? A native k-NN index only pays off if the similarity
		// arithmetic dominates. If the cost is reading and parsing every stored vector, an approximate
		// index fixes nothing and the stored representation is the real target.
		IVectorStoreReader reader = new SqliteBlobVectorStoreReader();

		watch.Restart();
		IReadOnlyList<StoredVector> candidates = reader.ReadVectorsByModelVersion(database.DatabasePath, modelVersionId);
		Int64 readMs = watch.ElapsedMilliseconds;
		this.Log($"read + parse {candidates.Count} vectors (scoring read, no joins): {readMs} ms");

		// Warm read, so the number is the query and the parsing rather than the page cache.
		watch.Restart();
		candidates = reader.ReadVectorsByModelVersion(database.DatabasePath, modelVersionId);
		Int64 warmReadMs = watch.ElapsedMilliseconds;
		this.Log($"read + parse again (warm): {warmReadMs} ms");

		// The similarity arithmetic on its own, over vectors already in memory. This is the only part
		// an approximate nearest-neighbour index could attack, so it decides whether one is worth having.
		IReadOnlyList<Single> queryVector = vectorizer.Vectorize(["query about content"], EmbeddingKind.Query)[0].Vector;

		watch.Restart();
		for (Int32 i = 0; i < candidates.Count; i++)
		{
			_ = Cosine(queryVector, candidates[i].Vector);
		}

		this.Log($"cosine over {candidates.Count} in-memory vectors: {watch.ElapsedMilliseconds} ms");

		CosineRetrievalQuery retrieval = new(vectorizer);
		RetrievalOptions options = new(TopK: 5);

		// Warm once, then take the median of several. A single timing on a cold page cache measures
		// the disk, not the query.
		retrieval.Search(database.DatabasePath, "alpha beta gamma", options);

		List<Int64> timings = [];
		for (Int32 i = 0; i < 5; i++)
		{
			watch.Restart();
			retrieval.Search(database.DatabasePath, $"query number {i} about content", options);
			timings.Add(watch.ElapsedMilliseconds);
		}

		timings.Sort();
		this.Log($"retrieval: p50 {timings[timings.Count / 2]} ms over {candidates.Count} vectors " +
			$"(min {timings[0]}, max {timings[^1]})");
		this.Log($"database size: {new FileInfo(database.DatabasePath).Length / 1024 / 1024} MB");
		this.Log($"peak working set: {Process.GetCurrentProcess().PeakWorkingSet64 / 1024 / 1024} MB");
	}

	/// <summary>Live bytes after a full collect — what the run is holding, not what it has churned.</summary>
	private static Int64 LiveBytes() => GC.GetTotalMemory(forceFullCollection: true);

	/// <summary>
	/// Prose-shaped filler with a stable per-file vocabulary. The content only has to chunk and embed
	/// like real text; it does not have to mean anything.
	/// </summary>
	private static Double Cosine(IReadOnlyList<Single> left, IReadOnlyList<Single> right)
	{
		Double dot = 0, leftNorm = 0, rightNorm = 0;

		for (Int32 i = 0; i < left.Count && i < right.Count; i++)
		{
			dot += left[i] * right[i];
			leftNorm += left[i] * left[i];
			rightNorm += right[i] * right[i];
		}

		return leftNorm <= 0 || rightNorm <= 0 ? 0 : dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
	}

	private const Int32 TokensPerFile = 1200;

	private static void GenerateCorpus(TempFolder folder, Int32 fileCount)
	{
		String[] words =
		[
			"alpha", "beta", "gamma", "delta", "epsilon", "index", "vector", "chunk", "folder",
			"query", "storage", "retrieval", "embedding", "document", "content", "matrix",
		];

		for (Int32 i = 0; i < fileCount; i++)
		{
			// A directory per hundred files, so the walk has a realistic shape rather than one flat
			// directory of thousands.
			String directory = folder.Combine($"dir{i / 100:D3}");
			Directory.CreateDirectory(directory);

			Random random = new(i);
			String[] tokens = new String[TokensPerFile];
			for (Int32 t = 0; t < tokens.Length; t++)
			{
				tokens[t] = words[random.Next(words.Length)];
			}

			File.WriteAllText(Path.Combine(directory, $"doc{i:D5}.md"), String.Join(' ', tokens));
		}
	}

	private void Log(String line)
	{
		this._output.WriteLine(line);
		Console.WriteLine($"[bench] {line}");
	}
}
