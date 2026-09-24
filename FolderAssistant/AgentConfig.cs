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

	/// <summary>The agent roster and how a turn is routed through it (SPEC-100).</summary>
	public WorkflowConfig Workflow { get; init; } = new();

	/// <summary>
	/// Which bundle of module implementations to compose: vectorizer, vector store, and
	/// retrieval strategy. See <see cref="CompositionProfiles"/> for the valid names. A named
	/// profile that is unknown or cannot run on this platform is a startup failure, never a silent
	/// fallback. Unset, the platform's default runs (<see cref="CompositionProfiles.Default"/>, or its
	/// blob twin where the native store has no binary) and the composition root says which.
	/// </summary>
	public String? Profile { get; init; }

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

	/// <summary>
	/// Vector dimension of the in-process vectorizers: the width the programmable one produces, and the
	/// target rank the corpus-fitted one reduces to (bounded by what the corpus can support).
	/// </summary>
	public Int32 VectorDimension { get; init; } = 64;

	/// <summary>Model version identifier the programmable vectorizer persists vectors under.</summary>
	public String ModelVersionId { get; init; } = "programmable-v1";

	/// <summary>
	/// Model version identifier the corpus-fitted vectorizer persists vectors and its fit under. Its own,
	/// not the programmable one's: the two embed into unrelated spaces, and vectors are compared only
	/// within one model version, so sharing an id would let a profile switch mix them.
	/// </summary>
	public String LsaModelVersionId { get; init; } = "lsa-v1";

	// ── Ollama embedding provider — read only by the ollama-* composition profiles (SPEC-162) ──

	/// <summary>Base URL of the local Ollama OpenAI-compatible embeddings endpoint.</summary>
	public String OllamaEndpoint { get; init; } = "http://localhost:11434/v1";

	/// <summary>
	/// Permits <see cref="OllamaEndpoint"/> to name a host that is not on loopback. Off, and an endpoint
	/// that is not loopback stops the application at startup with the setting named
	/// (<c>EmbeddingEndpointGuard</c>, <c>SPEC-162</c>).
	///
	/// <para>
	/// The promise this protects is the system's headline one: the text of every indexed file is what
	/// goes to the embedding endpoint, so a free-form endpoint with nothing checking it means the
	/// offline guarantee is only a default. An operator who has a GPU host on their own network can
	/// have it — by saying so here, where the decision is visible in configuration and reported by
	/// <c>GET /</c> — rather than by leaving a setting at a value nobody looked at.
	/// </para>
	/// </summary>
	public Boolean AllowRemoteEmbeddingEndpoint { get; init; }

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
	/// The bound on one embed call, whatever its batch size, after which the call is abandoned as a
	/// timeout and the work is retried. Without one, a call that never returned would hold a delivery in
	/// flight forever with its attempts at zero — retry, backoff and the attempt limit never engage — and
	/// during the first pass would leave the index <c>Building</c> for the life of the process.
	///
	/// <para>
	/// Sized for a full embed window (<see cref="EmbeddingBatchSizeChunks"/>) of the slowest chunks a
	/// real folder holds, which is what moved it from 120 s to 600 s on 2026-09-24. The cost of a chunk
	/// tracks the model tokens in it, not the chunk count: measured here, CPU-only, on full 256-token
	/// windows, about 2.1 s for an English chunk and 4.3 s for a Russian one — where the 20-document
	/// English corpus of 2026-09-16 gave 600 ms for its much shorter chunks. A window of 64 mixed-language
	/// chunks is therefore about 190 s of work, and under the old 120 s every window timed out, retried
	/// and timed out again, so a folder of Russian or Turkish text could not be indexed at all while each
	/// failure looked like a hung server. Raise it further with the window, or on a slower machine.
	/// </para>
	///
	/// <para>
	/// It is also the client's own network timeout, and nothing retries underneath the call: the startup
	/// probe and the outbox dispatcher retry above it (<c>SPEC-162</c>), so a server that is not there
	/// costs one connection per attempt rather than this whole deadline. The probe embeds two words
	/// rather than a window, so it bounds itself far shorter than this
	/// (<c>OllamaEmbeddingVectorizer.HealthCheckDeadline</c>) — otherwise raising this value would make a
	/// server that accepts connections and never answers take half an hour to be reported.
	/// </para>
	/// </summary>
	public Int32 OllamaTimeoutSeconds { get; init; } = 600;

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

/// <summary>
/// The roster and routing configuration (SPEC-100). The default roster is not here: it lives in code,
/// because .NET merges configuration arrays by index, so a roster shipped in <c>appsettings.json</c>
/// would be merged <em>into</em> an operator's own entries rather than replaced by them.
/// </summary>
internal record WorkflowConfig
{
	/// <summary>
	/// Run the default roster from code — an orchestrator delegating to a reader and a mutator — when
	/// no <see cref="Agents"/> are configured. Off, one agent holding every tool answers each turn. Off
	/// by default until the roster's cost against the single agent has been measured.
	/// </summary>
	public Boolean UseDefaultRoster { get; init; }

	/// <summary>
	/// The agent every turn enters. Required when the roster holds more than one agent; a roster of one
	/// routes to it. Never "the first one".
	/// </summary>
	public String? Coordinator { get; init; }

	/// <summary>The operator's roster. Any entry here replaces the default roster entirely.</summary>
	public List<AgentEntryConfig> Agents { get; init; } = [];
}

/// <summary>One agent in the roster: its role, what it may call, whom it may delegate to, and its provider.</summary>
internal record AgentEntryConfig
{
	/// <summary>The agent's name, unique in the roster; the key a delegation names it by.</summary>
	public String Name { get; init; } = "";

	/// <summary>What the agent does, in a sentence. It is the description of the tool that delegates to it.</summary>
	public String? Description { get; init; }

	/// <summary>The agent's system prompt, verbatim. Unset, one is built from the name and description.</summary>
	public String? SystemPrompt { get; init; }

	/// <summary>The tools the agent may call, by name; none means none.</summary>
	public List<String> Tools { get; init; } = [];

	/// <summary>The agents this one may delegate to, by name; one delegation tool per target.</summary>
	public List<String> Delegates { get; init; } = [];

	/// <summary>Provider settings this agent declares for itself; whatever it leaves unset is the root's.</summary>
	public ProviderOverrideConfig? Provider { get; init; }
}

/// <summary>
/// The provider fields an agent may override, each nullable so that an unset field is told from a set
/// one: an agent inherits the root provider for anything it does not declare. Its role — name,
/// description, prompt — is never inherited, because the role is what makes it a different agent.
/// </summary>
internal record ProviderOverrideConfig
{
	public ProviderConfig.AiProviderType? Type { get; init; }

	public String? Endpoint { get; init; }

	public String? ApiKey { get; init; }

	public String? DeploymentName { get; init; }

	public Double? Temperature { get; init; }

	public Int32? MaxTokens { get; init; }

	/// <summary>The root provider with every field this override declares put in its place.</summary>
	public ProviderConfig Apply(ProviderConfig root)
	{
		ArgumentNullException.ThrowIfNull(root);

		return root with
		{
			Type = this.Type ?? root.Type,
			Endpoint = this.Endpoint ?? root.Endpoint,
			ApiKey = this.ApiKey ?? root.ApiKey,
			DeploymentName = this.DeploymentName ?? root.DeploymentName,
			Temperature = this.Temperature ?? root.Temperature,
			MaxTokens = this.MaxTokens ?? root.MaxTokens,
		};
	}
}
