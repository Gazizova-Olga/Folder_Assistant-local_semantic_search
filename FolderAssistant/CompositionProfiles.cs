using FolderAssistant.Embedding;
using FolderAssistant.Embedding.Lsa;
using FolderAssistant.Indexing;
using FolderAssistant.Persistence;
using FolderAssistant.Retrieval;

namespace FolderAssistant;

/// <summary>
/// One runnable combination of module implementations: which vectorizer embeds, which store holds
/// the vectors, and which strategy retrieves them.
///
/// <para>
/// A profile is a <em>bundle</em>, not a set of independent switches. Per-module flags would
/// describe a combinatorial space in which most points are meaningless and some are actively
/// dangerous — a reader looking in <c>chunk_vector</c> while the writer fills a <c>vec0</c> table
/// does not fail, it reports that nothing has ever been embedded and re-embeds the whole folder on
/// every run. Enumerating the combinations in code means an invalid one cannot be named, and every
/// runnable configuration has a name that can be put in a benchmark result.
/// </para>
/// </summary>
internal sealed record ModuleSet(
	String Name,
	Func<IndexingConfig, IVectorizer> CreateVectorizer,
	Func<IVectorStoreWriter> CreateVectorStoreWriter,
	Func<IVectorStoreReader> CreateVectorStoreReader,
	Func<IVectorizer, IVectorStoreReader, IIndexState?, IRetrievalQuery> CreateRetrievalQuery,
	Func<Boolean> IsAvailable,
	String? UnavailableReason = null);

/// <summary>The enumerated set of module combinations this binary can compose.</summary>
internal static class CompositionProfiles
{
	/// <summary>
	/// The default, and deliberately the combination with no native dependency: it runs everywhere
	/// the managed code runs. It is not the fastest — measured here, <c>sqlite-vec</c> retrieves in
	/// 21 ms where the blob backend takes 325 ms at 72,000 vectors (<c>SPEC-131</c>) — so promoting
	/// it is a live decision, blocked on the native binary's platform coverage rather than on speed.
	/// </summary>
	public const String Default = "programmable-blob";

	private const String NoNativeBinary =
		"the sqlite-vec native extension ships no binary for this platform (no win-arm64, no musl build)";

	private static readonly IReadOnlyDictionary<String, ModuleSet> All =
		new Dictionary<String, ModuleSet>(StringComparer.OrdinalIgnoreCase)
		{
			[Default] = new(
				Name: Default,
				CreateVectorizer: static indexing => new ProgrammableEmbeddingVectorizer(
					indexing.ModelVersionId, indexing.VectorDimension),
				CreateVectorStoreWriter: static () => new SqliteBlobVectorStoreWriter(),
				CreateVectorStoreReader: static () => new SqliteBlobVectorStoreReader(),
				CreateRetrievalQuery: static (vectorizer, reader, indexState)
					=> new CosineRetrievalQuery(vectorizer, reader, indexState),
				IsAvailable: static () => true),

			["programmable-vec"] = new(
				Name: "programmable-vec",
				CreateVectorizer: static indexing => new ProgrammableEmbeddingVectorizer(
					indexing.ModelVersionId, indexing.VectorDimension),
				CreateVectorStoreWriter: static () => new SqliteVecVectorStoreWriter(),
				CreateVectorStoreReader: static () => new SqliteVecVectorStoreReader(),
				CreateRetrievalQuery: static (vectorizer, reader, indexState)
					=> new SqliteVecRetrievalQuery(vectorizer, reader, indexState),
				IsAvailable: static () => SqliteVecExtension.IsAvailable,
				UnavailableReason: NoNativeBinary),

			["lsa-blob"] = new(
				Name: "lsa-blob",
				CreateVectorizer: static indexing => LsaEmbeddingVectorizer.CreateForFitting(
					indexing.ModelVersionId, indexing.VectorDimension),
				CreateVectorStoreWriter: static () => new SqliteBlobVectorStoreWriter(),
				CreateVectorStoreReader: static () => new SqliteBlobVectorStoreReader(),
				CreateRetrievalQuery: static (vectorizer, reader, indexState)
					=> new CosineRetrievalQuery(vectorizer, reader, indexState),
				IsAvailable: static () => true),

			["lsa-vec"] = new(
				Name: "lsa-vec",
				CreateVectorizer: static indexing => LsaEmbeddingVectorizer.CreateForFitting(
					indexing.ModelVersionId, indexing.VectorDimension),
				CreateVectorStoreWriter: static () => new SqliteVecVectorStoreWriter(),
				CreateVectorStoreReader: static () => new SqliteVecVectorStoreReader(),
				CreateRetrievalQuery: static (vectorizer, reader, indexState)
					=> new SqliteVecRetrievalQuery(vectorizer, reader, indexState),
				IsAvailable: static () => SqliteVecExtension.IsAvailable,
				UnavailableReason: NoNativeBinary),

			// Local Ollama, qwen3-embedding:0.6b (SPEC-162). Not fittable — the model is pretrained, so
			// there is no corpus fit and a query needs nothing restored before it can be embedded.
			//
			// "Available" here means the code path runs everywhere. Whether the local server is reachable
			// and has the model pulled is a runtime condition, not a platform one, and it is not something
			// Resolve can answer without making a network call at startup.
			["ollama-blob"] = new(
				Name: "ollama-blob",
				CreateVectorizer: static indexing => new OllamaEmbeddingVectorizer(
					indexing.OllamaEndpoint,
					indexing.OllamaModel,
					indexing.OllamaModelVersionId,
					indexing.OllamaEmbeddingDimension),
				CreateVectorStoreWriter: static () => new SqliteBlobVectorStoreWriter(),
				CreateVectorStoreReader: static () => new SqliteBlobVectorStoreReader(),
				CreateRetrievalQuery: static (vectorizer, reader, indexState)
					=> new CosineRetrievalQuery(vectorizer, reader, indexState),
				IsAvailable: static () => true),

			["ollama-vec"] = new(
				Name: "ollama-vec",
				CreateVectorizer: static indexing => new OllamaEmbeddingVectorizer(
					indexing.OllamaEndpoint,
					indexing.OllamaModel,
					indexing.OllamaModelVersionId,
					indexing.OllamaEmbeddingDimension),
				CreateVectorStoreWriter: static () => new SqliteVecVectorStoreWriter(),
				CreateVectorStoreReader: static () => new SqliteVecVectorStoreReader(),
				CreateRetrievalQuery: static (vectorizer, reader, indexState)
					=> new SqliteVecRetrievalQuery(vectorizer, reader, indexState),
				IsAvailable: static () => SqliteVecExtension.IsAvailable,
				UnavailableReason: NoNativeBinary),
		};

	/// <summary>Every profile name, whether or not it can run on this platform.</summary>
	public static IEnumerable<String> Names => All.Keys;

	/// <summary>
	/// Resolves a profile by name, or throws.
	///
	/// <para>
	/// It throws in both failure cases — an unknown name, and a profile whose implementation is
	/// unavailable on this platform — rather than falling back to the default. A silent fallback
	/// would run a configuration nobody asked for while reporting success, and the result would be a
	/// <em>working</em> system answering from a different embedding space than the operator
	/// believes: the silently-plausible wrong answer this system is built against
	/// (<c>SPEC-000</c>).
	/// </para>
	/// </summary>
	public static ModuleSet Resolve(String? name)
	{
		String requested = String.IsNullOrWhiteSpace(name) ? Default : name;

		if (!All.TryGetValue(requested, out ModuleSet? profile))
		{
			throw new InvalidOperationException(
				$"Unknown composition profile '{requested}'. Available profiles: {String.Join(", ", All.Keys)}.");
		}

		if (!profile.IsAvailable())
		{
			throw new PlatformNotSupportedException(
				$"Composition profile '{profile.Name}' cannot run here: {profile.UnavailableReason}. " +
				$"Profiles that can: {String.Join(", ", All.Where(static p => p.Value.IsAvailable()).Select(static p => p.Key))}.");
		}

		return profile;
	}
}
