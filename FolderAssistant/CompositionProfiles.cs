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

/// <summary>
/// What resolving the default produced: the profile, and — when the default itself cannot run on this
/// platform — a note saying which profile was wanted and which is running. The note is what keeps the
/// fallback from being silent: the composition root logs it and the root endpoint reports it.
/// </summary>
internal sealed record DefaultResolution(ModuleSet Profile, String? FallbackNote);

/// <summary>The enumerated set of module combinations this binary can compose.</summary>
internal static class CompositionProfiles
{
	/// <summary>
	/// The default: the pretrained model served by a local Ollama, over the native k-NN store. The only
	/// embedder here that retrieves a passage sharing none of the query's words (<c>docs/benchmarks</c>);
	/// the price is a server that has to be installed, running and holding the model. That is a runtime
	/// prerequisite, not a platform one: the startup probe turns an absent server into a failed index with
	/// an actionable message, never into a different embedder. Where the native store has no binary —
	/// win-arm64, musl — the default is <see cref="DefaultFallback"/>, and the fallback is said, never silent.
	/// </summary>
	public const String Default = "ollama-vec";

	/// <summary>
	/// The same embedder over the blob store: the default's twin with no native dependency, run in its
	/// place only when nothing was configured and the native store cannot load here. Same embedding
	/// space, so nothing an operator believed about their vectors changes — only the store and its speed.
	/// </summary>
	public const String DefaultFallback = "ollama-blob";

	private const String NoNativeBinary =
		"the sqlite-vec native extension ships no binary for this platform (no win-arm64, no musl build)";

	private static readonly IReadOnlyDictionary<String, ModuleSet> All =
		new Dictionary<String, ModuleSet>(StringComparer.OrdinalIgnoreCase)
		{
			["programmable-blob"] = new(
				Name: "programmable-blob",
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
					indexing.LsaModelVersionId, indexing.VectorDimension),
				CreateVectorStoreWriter: static () => new SqliteBlobVectorStoreWriter(),
				CreateVectorStoreReader: static () => new SqliteBlobVectorStoreReader(),
				CreateRetrievalQuery: static (vectorizer, reader, indexState)
					=> new CosineRetrievalQuery(vectorizer, reader, indexState),
				IsAvailable: static () => true),

			["lsa-vec"] = new(
				Name: "lsa-vec",
				CreateVectorizer: static indexing => LsaEmbeddingVectorizer.CreateForFitting(
					indexing.LsaModelVersionId, indexing.VectorDimension),
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
					indexing.OllamaEmbeddingDimension,
					TimeSpan.FromSeconds(indexing.OllamaTimeoutSeconds),
					indexing.AllowRemoteEmbeddingEndpoint),
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
					indexing.OllamaEmbeddingDimension,
					TimeSpan.FromSeconds(indexing.OllamaTimeoutSeconds),
					indexing.AllowRemoteEmbeddingEndpoint),
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
	/// Resolves a profile by name, or throws; a blank name resolves the default, fallback included.
	///
	/// <para>
	/// A <em>named</em> profile throws in both failure cases — an unknown name, and a profile whose
	/// implementation is unavailable on this platform — and never falls back. A silent fallback would run
	/// a configuration nobody asked for while reporting success, and the result would be a <em>working</em>
	/// system answering from a different embedding space than the operator believes: the silently-plausible
	/// wrong answer this system is built against (<c>SPEC-000</c>). Only the default, which nobody named,
	/// may fall back — and <see cref="ResolveDefault"/> says so when it does.
	/// </para>
	/// </summary>
	public static ModuleSet Resolve(String? name)
		=> Resolve(name, static profile => profile.IsAvailable());

	/// <summary>
	/// The default for this platform: <see cref="Default"/> where its native store can load, otherwise
	/// <see cref="DefaultFallback"/> with a note naming both. The note is the whole difference between a
	/// fallback and a silent one.
	/// </summary>
	public static DefaultResolution ResolveDefault()
		=> ResolveDefault(static profile => profile.IsAvailable());

	internal static ModuleSet Resolve(String? name, Func<ModuleSet, Boolean> isAvailable)
	{
		if (String.IsNullOrWhiteSpace(name))
		{
			return ResolveDefault(isAvailable).Profile;
		}

		if (!All.TryGetValue(name, out ModuleSet? profile))
		{
			throw new InvalidOperationException(
				$"Unknown composition profile '{name}'. Available profiles: {String.Join(", ", All.Keys)}.");
		}

		if (!isAvailable(profile))
		{
			throw new PlatformNotSupportedException(
				$"Composition profile '{profile.Name}' cannot run here: {profile.UnavailableReason}. " +
				$"Profiles that can: {String.Join(", ", All.Where(p => isAvailable(p.Value)).Select(static p => p.Key))}.");
		}

		return profile;
	}

	internal static DefaultResolution ResolveDefault(Func<ModuleSet, Boolean> isAvailable)
	{
		ModuleSet preferred = All[Default];
		if (isAvailable(preferred))
		{
			return new DefaultResolution(preferred, null);
		}

		ModuleSet fallback = All[DefaultFallback];

		return new DefaultResolution(
			fallback,
			$"No profile is configured and the default '{Default}' cannot run here ({preferred.UnavailableReason}); "
			+ $"running '{DefaultFallback}' instead: the same embedder over the blob store. Set FolderAssistant:Profile to choose explicitly.");
	}
}
