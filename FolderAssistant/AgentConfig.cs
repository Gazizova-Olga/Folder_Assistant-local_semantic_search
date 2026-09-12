namespace FolderAssistant;

/// <summary>Top-level configuration, bound from the <c>FolderAssistant</c> configuration section.</summary>
internal record AgentConfig
{
	/// <summary>The configuration section this binds from.</summary>
	internal const String SectionName = "FolderAssistant";

	/// <summary>Configuration for the underlying AI provider (model, endpoint, credentials, sampling).</summary>
	public ProviderConfig Provider { get; init; } = new();

	/// <summary>Persistence configuration for the folder-scoped local database.</summary>
	public PersistenceConfig Persistence { get; init; } = new();

	/// <summary>Local indexing configuration (scan, chunk, tokenize, and embedding persistence).</summary>
	public IndexingConfig Indexing { get; init; } = new();

	/// <summary>
	/// Which bundle of module implementations to compose: vectorizer, vector store, and
	/// retrieval strategy. See <see cref="CompositionProfiles"/> for the valid names. An
	/// unknown or platform-unavailable profile is a startup failure, never a silent fallback.
	/// </summary>
	public String Profile { get; init; } = CompositionProfiles.Default;

	/// <summary>HTTP port the web server listens on. Defaults to <c>5000</c>.</summary>
	public Int32 Port { get; init; } = 5000;

	/// <summary>HTTP client timeout in seconds. Defaults to <c>30</c>.</summary>
	public Double ConnectionTimeoutSeconds { get; init; } = 30;

	/// <summary>
	/// Fully overrides the auto-generated system prompt. When <see langword="null"/>, a prompt is
	/// constructed from <see cref="AgentName"/> and <see cref="AgentDescription"/>.
	/// </summary>
	public String? SystemPrompt { get; init; }

	/// <summary>Display name of the agent, used in the auto-generated system prompt.</summary>
	public String? AgentName { get; init; }

	/// <summary>Short description of the agent's role, used in the auto-generated system prompt.</summary>
	public String? AgentDescription { get; init; }

	/// <summary>HTTP client timeout derived from <see cref="ConnectionTimeoutSeconds"/>.</summary>
	public TimeSpan ConnectionTimeout => TimeSpan.FromSeconds(this.ConnectionTimeoutSeconds);

	/// <summary>Resolves the effective analyzed folder path, falling back to the current directory.</summary>
	public String ResolveAnalyzedFolderPath()
	{
		if (!String.IsNullOrWhiteSpace(this.Persistence.AnalyzedFolderPath))
		{
			return Path.GetFullPath(this.Persistence.AnalyzedFolderPath);
		}

		return Directory.GetCurrentDirectory();
	}
}

/// <summary>Configuration for folder-scoped manifest/vector database initialization.</summary>
internal record PersistenceConfig
{
	/// <summary>Optional analyzed folder path override. If empty, the current working directory is used.</summary>
	public String? AnalyzedFolderPath { get; init; }

	/// <summary>Subfolder name inside the analyzed folder used for persistence artifacts.</summary>
	public String MetadataFolderName { get; init; } = ".folderassistant";

	/// <summary>SQLite database file name used for manifest and vector tables.</summary>
	public String DatabaseFileName { get; init; } = "manifest.db";
}

/// <summary>Configuration for the AI provider used to service chat requests.</summary>
internal record ProviderConfig
{
	/// <summary>Selects between the supported AI provider backends.</summary>
	public enum AiProviderType
	{
		/// <summary>Standard OpenAI API, or any compatible third-party endpoint.</summary>
		OpenAI,

		/// <summary>Azure OpenAI Service.</summary>
		Azure,
	}

	/// <summary>Selects the AI provider backend. Defaults to <see cref="AiProviderType.Azure"/>.</summary>
	public AiProviderType Type { get; init; } = AiProviderType.Azure;

	/// <summary>Base URL of the provider endpoint.</summary>
	/// <remarks>
	/// Required for <see cref="AiProviderType.Azure"/>; optional for <see cref="AiProviderType.OpenAI"/>,
	/// where it overrides the default OpenAI base URL.
	/// </remarks>
	public String? Endpoint { get; init; }

	/// <summary>API key used to authenticate with the provider.</summary>
	public String? ApiKey { get; init; }

	/// <summary>Model or deployment name to target. Defaults to <c>"gpt-chat"</c>.</summary>
	public String DeploymentName { get; init; } = "gpt-chat";

	/// <summary>Sampling temperature. When <see langword="null"/>, the model's default is used.</summary>
	public Double? Temperature { get; init; }

	/// <summary>Maximum output tokens per request. When <see langword="null"/>, the model's default is used.</summary>
	public Int32? MaxTokens { get; init; }
}

/// <summary>Configuration for the local programmable embedding indexing pipeline.</summary>
internal record IndexingConfig
{
	/// <summary>Enables local folder indexing at startup.</summary>
	public Boolean Enabled { get; init; } = true;

	/// <summary>Maximum size for text files included in indexing.</summary>
	public Int64 MaxTextFileSizeBytes { get; init; } = 1_048_576;

	/// <summary>Chunk size in tokens for indexing.</summary>
	public Int32 ChunkSizeTokens { get; init; } = 256;

	/// <summary>Token overlap between consecutive chunks.</summary>
	public Int32 ChunkOverlapTokens { get; init; } = 32;

	/// <summary>
	/// How many chunks the corpus pass gathers into one embed call, coalesced across files rather than
	/// one call per file.
	///
	/// <para>
	/// It is a round-trip lever, not a throughput trick. An embedder reached over a socket charges per
	/// call and can embed a whole array in one, so the number of calls is what a first index costs;
	/// measured on this machine, the embedder is essentially the entire cost of that first pass. An
	/// in-process embedder is indifferent to the value.
	/// </para>
	///
	/// <para>
	/// Bounded on purpose. Only this many chunks' text is ever held at once, so the pass keeps streaming
	/// rather than accumulating the corpus, which is the property <c>SPEC-120</c> makes. A value of one
	/// reproduces the old per-file behaviour.
	/// </para>
	/// </summary>
	public Int32 EmbeddingBatchSizeChunks { get; init; } = 64;

	/// <summary>Vector dimension produced by the programmable vectorizer.</summary>
	public Int32 VectorDimension { get; init; } = 64;

	/// <summary>Deterministic model version identifier used for persisted vectors.</summary>
	public String ModelVersionId { get; init; } = "programmable-v1";

	// ── Ollama embedding provider — read only by the ollama-* composition profiles (SPEC-162) ──

	/// <summary>Base URL of the local Ollama OpenAI-compatible embeddings endpoint.</summary>
	public String OllamaEndpoint { get; init; } = "http://localhost:11434/v1";

	/// <summary>The Ollama embedding model. The default is the one this system targets.</summary>
	public String OllamaModel { get; init; } = "qwen3-embedding:0.6b";

	/// <summary>
	/// The <c>model_version_id</c> Ollama vectors are stored and queried under. It is separate from the
	/// model name because vectors are keyed by version, not by whatever the server happens to be serving.
	/// </summary>
	public String OllamaModelVersionId { get; init; } = "qwen3-embedding-0.6b-v1";

	/// <summary>
	/// The vector width <see cref="OllamaModel"/> emits. For <c>qwen3-embedding:0.6b</c> it is 1024, fixed
	/// by the weights — this setting exists to be checked against, not to choose. The vectorizer validates
	/// that the model really emits this width and fails rather than storing a wrong-width vector.
	/// </summary>
	public Int32 OllamaEmbeddingDimension { get; init; } = 1024;

	/// <summary>
	/// How long a changed file must go untouched before its change is processed, so a burst of edits to
	/// one file costs one delivery rather than one per event.
	/// </summary>
	public Int32 DebounceMilliseconds { get; init; } = 750;

	/// <summary>
	/// How long to wait before trying a failed first index again. The cause is usually outside this
	/// process and usually temporary — an embedding backend that has not finished starting, a model
	/// still being pulled — and without a retry the index reports <c>Failed</c> for the life of the
	/// process even though the next attempt would succeed. Zero disables retrying, leaving the first
	/// attempt the only one.
	/// </summary>
	public Int32 FailedIndexRetryIntervalSeconds { get; init; } = 30;

	/// <summary>
	/// How often the folder is compared in full against the index, as a safety net for changes the
	/// watcher never reports — events dropped when its buffer overflows, and edits that arrive in a
	/// shape it does not recognise. Zero disables it; the comparison at start still runs.
	/// </summary>
	public Int32 ReconciliationIntervalSeconds { get; init; } = 300;
}
