using System.Diagnostics.CodeAnalysis;
using FolderAssistant.Agents;
using FolderAssistant.Embedding;
using FolderAssistant.Extraction;
using FolderAssistant.Indexing;
using FolderAssistant.Indexing.Outbox;
using FolderAssistant.Indexing.Scanning;
using FolderAssistant.Indexing.Watching;
using FolderAssistant.Persistence;
using FolderAssistant.Retrieval;
using FolderAssistant.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;

namespace FolderAssistant;

/// <summary>
/// The entry point, and the name a test host uses to boot this application
/// (<c>WebApplicationFactory&lt;Program&gt;</c>).
/// </summary>
internal sealed class Program
{
	private static void Main(String[] args)
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

		// Bound lazily, through IOptions, rather than read straight off builder.Configuration here.
		// An eager bind freezes the values before the host is built, which silently discards every
		// configuration source added afterwards — including the one a test host injects, which is
		// how a host under test ends up running against a developer's own local settings instead of
		// the ones the test asked for.
		builder.Services.Configure<AgentConfig>(builder.Configuration.GetSection(AgentConfig.SectionName));
		builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<AgentConfig>>().Value);

		// The one source of which files this system reads and how each is decoded. Every walker over the
		// folder — the whole-folder pass, the per-file delivery, the watcher's filter, the text search — is
		// handed this instance, so no two of them can disagree about what the corpus is.
		builder.Services.AddSingleton(TextExtractorRegistry.Default);

		builder.Services.AddSingleton<IndexState>();
		builder.Services.AddSingleton<IIndexState>(sp => sp.GetRequiredService<IndexState>());

		// Which implementations run is decided here, by name, from one configuration value. The
		// profile is a bundle rather than a set of independent switches, so a mismatched combination
		// cannot be expressed. A named profile that is unknown or cannot run here throws and never
		// falls back, because a silent fallback would serve answers from a different embedding space
		// than the operator believes they configured. Only the default, which nobody named, may fall
		// back to its blob twin — logged here at warning, and reported by GET / — so the one case
		// where the running profile is not the expected one is the one case that is said out loud.
		builder.Services.AddSingleton(sp =>
		{
			String? configured = sp.GetRequiredService<AgentConfig>().Profile;
			DefaultResolution resolution = String.IsNullOrWhiteSpace(configured)
				? CompositionProfiles.ResolveDefault()
				: new DefaultResolution(CompositionProfiles.Resolve(configured), null);

			ILogger<Program> logger = sp.GetRequiredService<ILogger<Program>>();
			if (resolution.FallbackNote is not null)
			{
				logger.LogWarning("{FallbackNote}", resolution.FallbackNote);
			}

			logger.LogInformation("Composition profile: {Profile}", resolution.Profile.Name);

			return resolution;
		});
		builder.Services.AddSingleton(sp => sp.GetRequiredService<DefaultResolution>().Profile);

		// One vectorizer instance, shared by indexing and retrieval. For a corpus-fitted vectorizer
		// this is load-bearing rather than an economy: the pipeline loads the fit onto the instance,
		// and a query embedded by an unfitted one lands in a different space than the vectors it is
		// being compared against.
		builder.Services.AddSingleton<IEmbeddingTelemetry, LoggerEmbeddingTelemetry>();

		// Wrapped, not constructed bare: Wrap re-exposes whichever optional capability the composed
		// vectorizer implements. A plain wrapper would answer "no" to the fit and health-check type
		// tests while still returning good vectors, which disables both silently.
		builder.Services.AddSingleton(sp => EmbeddingTelemetryVectorizer.Wrap(
			sp.GetRequiredService<ModuleSet>().CreateVectorizer(sp.GetRequiredService<AgentConfig>().Indexing),
			sp.GetRequiredService<IEmbeddingTelemetry>()));

		// The context-assembly stage. Composed but not yet called: the consumer that would run it over
		// retrieval output is a search tool, which belongs to the agent work.
		builder.Services.AddSingleton<IContextReduction, TokenBudgetContextReducer>();

		builder.Services.AddSingleton(sp => sp.GetRequiredService<ModuleSet>().CreateVectorStoreWriter());
		builder.Services.AddSingleton(sp => sp.GetRequiredService<ModuleSet>().CreateVectorStoreReader());

		// Per-call retrieval telemetry, the same seam shape the embedding sink above uses.
		builder.Services.AddSingleton<IRetrievalTelemetry, LoggerRetrievalTelemetry>();

		// Retrieval was implemented and tested but composed nowhere, which SPEC-000 called the
		// central open item. This registers it, so it is resolvable from the container. The gap that
		// remains is that nothing on the request path asks it anything yet.
		//
		// Wrapped, so whichever backend the profile chose is timed by the same instrument. Measuring
		// inside each backend instead would make the two sets of numbers incomparable, which is the
		// one thing they exist to be.
		builder.Services.AddSingleton(sp => RetrievalTelemetryQuery.Wrap(
			sp.GetRequiredService<ModuleSet>().CreateRetrievalQuery(
				sp.GetRequiredService<IVectorizer>(),
				sp.GetRequiredService<IVectorStoreReader>(),
				sp.GetRequiredService<IIndexState>()),
			sp.GetRequiredService<IRetrievalTelemetry>()));

		// The retrieval meter, served at GET /metrics below. Metrics only: nothing in this assembly
		// emits a trace yet, and registering a tracing pipeline with no source to read would export
		// an empty signal that looks like a broken one.
		builder.Services.AddOpenTelemetry()
			.WithMetrics(metrics =>
			{
				metrics.AddMeter(LoggerRetrievalTelemetry.MeterName);
				metrics.AddMeter(LoggerTurnTelemetry.MeterName);
				metrics.AddPrometheusExporter();
			});

		builder.Services.AddSingleton(sp =>
		{
			AgentConfig config = sp.GetRequiredService<AgentConfig>();

			DatabaseBootstrapResult database = new FolderDatabaseBootstrapper()
				.EnsureInitialized(config.ResolveAnalyzedFolderPath(), config.Persistence);

			Console.WriteLine(
				$"Database bootstrap: {(database.Created ? "created" : "reused")} at {database.DatabasePath}");

			return database;
		});

		// The store the indexing front end writes its file records and deliveries through. It is the
		// application's, over the same folder database as the chunks and vectors, so that recording a
		// change and queuing its delivery are one write.
		builder.Services.AddSingleton(sp =>
			new FolderIndexStore(sp.GetRequiredService<DatabaseBootstrapResult>().DatabasePath));

		// The application's half of the seam the front end delivers through: one file in, chunks and
		// vectors out. It shares the vectorizer and the vector store with the whole-folder pass below,
		// so a file embeds the same way whichever path indexed it.
		builder.Services.AddSingleton<IVectorizationService>(sp =>
		{
			AgentConfig config = sp.GetRequiredService<AgentConfig>();

			return new RagBridgeVectorizationService(
				config.ResolveAnalyzedFolderPath(),
				sp.GetRequiredService<DatabaseBootstrapResult>().DatabasePath,
				sp.GetRequiredService<IVectorizer>(),
				new FolderIndexRepository(sp.GetRequiredService<IVectorStoreWriter>()),
				sp.GetRequiredService<IVectorStoreReader>(),
				sp.GetRequiredService<TextExtractorRegistry>(),
				config.Indexing,
				config.Persistence.MetadataFolderName);
		});

		// The read tools over their own containment guard. Composed and resolved by nothing: the agent
		// that would reflect the holder's methods into tools does not exist yet. The text search takes
		// the scanner's size bound so it reads exactly the files the index does.
		builder.Services.AddSingleton(sp =>
		{
			AgentConfig config = sp.GetRequiredService<AgentConfig>();

			return new ReadTools(
				new WorkspacePathGuard(config.ResolveAnalyzedFolderPath(), config.Persistence.MetadataFolderName),
				sp.GetRequiredService<TextExtractorRegistry>(),
				config.Indexing.MaxTextFileSizeBytes);
		});

		// The mutation tools over a second guard of their own, over the same root. Two instances rather
		// than one shared is the split: which holder an agent is granted is what decides whether it can
		// change the folder, and the guard goes with the holder. Every completed write is reported to the
		// running front end below, so the index follows an edit this process made without rediscovering it.
		builder.Services.AddSingleton(sp =>
		{
			AgentConfig config = sp.GetRequiredService<AgentConfig>();

			return new MutationTools(
				new WorkspacePathGuard(config.ResolveAnalyzedFolderPath(), config.Persistence.MetadataFolderName),
				sp.GetRequiredService<IIndexChangeNotifier>());
		});

		// The search tools, over the composed retrieval query — the wrapped one, so a file-level search is
		// timed and refused by the same instrument and the same readiness check as a passage search. A
		// separate holder from the read tools because a search failure is fatal where a file failure is a
		// string, and the holder is what tells the two apart. Resolved by nothing yet, like the read tools.
		builder.Services.AddSingleton(sp => new SearchTools(
			sp.GetRequiredService<IRetrievalQuery>(),
			sp.GetRequiredService<DatabaseBootstrapResult>().DatabasePath));

		// Every tool the application can grant, each through the facade of its group — the file tools under
		// the string contract, the search tools under the fatal one — held by name so a roster entry's
		// allowlist can be narrowed to exactly what it says.
		builder.Services.AddSingleton(sp =>
		{
			ILogger<ToolFacade> toolLogger = sp.GetRequiredService<ILogger<ToolFacade>>();

			return new AgentToolCatalog(
			[
				.. ToolSet.ForFiles(sp.GetRequiredService<ReadTools>(), sp.GetRequiredService<MutationTools>(), toolLogger),
				.. ToolSet.ForSearch(sp.GetRequiredService<SearchTools>(), toolLogger),
			]);
		});

		// The roster, validated whole before the server listens (the startup filter below takes it): an
		// unknown coordinator, an unknown tool or delegate, a self-delegation or a cycle is a startup
		// failure, never a first-turn surprise. It needs no provider, so a keyless host still boots.
		builder.Services.AddSingleton(sp => Roster.Build(
			sp.GetRequiredService<AgentConfig>(),
			sp.GetRequiredService<AgentToolCatalog>().Names));

		// What builds a chat client for an agent's effective provider. Registered on its own so that it is
		// the one seam a host under test replaces to run a turn without a model.
		builder.Services.AddSingleton<Func<ProviderConfig, IChatClient>>(sp =>
		{
			AgentConfig config = sp.GetRequiredService<AgentConfig>();

			return provider => ProviderClientFactory.Create(provider, config.ConnectionTimeout);
		});

		// The agents themselves, one handle per roster entry over its own client. Nothing here connects,
		// and a configuration that cannot name a provider at all fails when the registry is first asked
		// for, with a sentence saying what is missing.
		builder.Services.AddSingleton(sp => new AgentRegistry(
			sp.GetRequiredService<Roster>(),
			sp.GetRequiredService<AgentToolCatalog>(),
			sp.GetRequiredService<Func<ProviderConfig, IChatClient>>(),
			sp.GetService<ILoggerFactory>()));

		builder.Services.AddSingleton(sp => new StaticWorkflowRoute(
			sp.GetRequiredService<Roster>(),
			sp.GetRequiredService<AgentRegistry>()));

		// The turn: the coordinator's session loaded, the agent run, the session saved, and the turn's
		// telemetry recorded inside the execution. Sessions live for the life of the process; the
		// conversation database is what replaces this store. The runner is what a front end talks to, and
		// no front end exists, so it is resolved by nothing.
		builder.Services.AddSingleton<ITurnTelemetry, LoggerTurnTelemetry>();
		builder.Services.AddSingleton<IAgentSessionStore, InMemoryAgentSessionStore>();
		builder.Services.AddSingleton<IAgentExecution, MicrosoftAgentExecution>();
		builder.Services.AddSingleton<WorkflowRunner>();

		// The front end: watcher, reconciler, per-change pipeline and outbox dispatcher, composed by
		// the library and started by the indexing service once the whole-folder pass has succeeded.
		builder.Services.AddSingleton<IFolderIndexer>(sp =>
		{
			AgentConfig config = sp.GetRequiredService<AgentConfig>();
			FolderIndexStore store = sp.GetRequiredService<FolderIndexStore>();

			// The extension list is the extraction registry's and the size bound the scanner's, from one
			// source of truth each. Left at the library's defaults, every binary in the folder would be
			// recorded, queued and delivered to a bridge that refuses each one, and an oversize file would
			// be read whole.
			FolderIndexerOptions options = new()
			{
				RootPath = config.ResolveAnalyzedFolderPath(),
				MetadataFolderName = config.Persistence.MetadataFolderName,
				IndexableExtensions = sp.GetRequiredService<TextExtractorRegistry>().Extensions,
				MaxContentBytes = config.Indexing.MaxTextFileSizeBytes,
				QuietWindow = TimeSpan.FromMilliseconds(config.Indexing.DebounceMilliseconds),
				ReconciliationInterval = TimeSpan.FromSeconds(config.Indexing.ReconciliationIntervalSeconds),
			};

			// The logger factory is optional to the library and present here: every loop it runs
			// survives its faults, and the log is the only place a loop failing every pass differs
			// from one with nothing to do.
			return new FolderIndexer(
				options,
				store,
				store,
				sp.GetRequiredService<IVectorizationService>(),
				new Sha256ContentHasher(),
				sp.GetService<ILoggerFactory>());
		});

		// The same instance, not a second one: a report is fed into the running front end's own
		// debouncer, so it has to be the object that owns it.
		builder.Services.AddSingleton<IIndexChangeNotifier>(sp => sp.GetRequiredService<IFolderIndexer>());

		// Whether indexing runs at all is decided when this factory executes, which is after the
		// configuration is final — not while the composition root is being written.
		builder.Services.AddSingleton<IHostedService>(sp =>
		{
			AgentConfig config = sp.GetRequiredService<AgentConfig>();
			IndexState indexState = sp.GetRequiredService<IndexState>();

			if (!config.Indexing.Enabled)
			{
				return new IndexingDisabledService(indexState);
			}

			String analyzedFolderPath = config.ResolveAnalyzedFolderPath();
			String databasePath = sp.GetRequiredService<DatabaseBootstrapResult>().DatabasePath;

			// The vectorizer and the store both come from the resolved profile, so the writer and
			// reader are a matched pair by construction. A reader looking somewhere other than where
			// the writer wrote would not fail — it would report that no file had ever been embedded,
			// and every file would re-embed on every run with the index still looking correct.
			FolderIndexingPipeline pipeline = new(
				sp.GetRequiredService<IVectorizer>(),
				sp.GetRequiredService<IVectorStoreWriter>(),
				sp.GetRequiredService<IVectorStoreReader>(),
				config.Persistence.MetadataFolderName,
				sp.GetRequiredService<TextExtractorRegistry>());

			// Only a network-bound embedder implements the probe seam; for the in-process ones this is
			// null and the first pass just starts. The cast is how the composition root avoids knowing
			// which kind the active profile built.
			return new FolderIndexingService(
				() => pipeline.Run(analyzedFolderPath, databasePath, config.Indexing),
				sp.GetRequiredService<IFolderIndexer>(),
				indexState,
				sp.GetRequiredService<IVectorizer>() as IEmbeddingHealthCheck,
				TimeSpan.FromSeconds(config.Indexing.FailedIndexRetryIntervalSeconds));
		});

		// The bootstrap has to finish before the first request is served. A startup filter is what
		// orders that: the host resolves it while starting, before the server listens, so taking the
		// bootstrap as a constructor dependency forces it into existence first. It cannot go at the
		// end of Main instead — a test host intercepts at builder.Build(), so nothing past that line
		// runs under test.
		builder.Services.AddTransient<IStartupFilter, StartupDependencies>();

		// The one value that must be known before the host is built, because Kestrel binds on it and
		// it cannot be late-bound. It does not change behaviour.
		Int32 port = builder.Configuration.GetValue<Int32?>($"{AgentConfig.SectionName}:Port") ?? new AgentConfig().Port;
		builder.WebHost.UseUrls($"http://localhost:{port}");

		WebApplication app = builder.Build();

		// The active profile is reported here so that what is running is one request away, fallback
		// included: the note is null unless the default could not run and its blob twin did.
		app.MapGet("/", (AgentConfig config, DefaultResolution profile) => Results.Ok(new
		{
			name = "Folder Assistant",
			analyzedFolder = config.ResolveAnalyzedFolderPath(),
			profile = profile.Profile.Name,
			profileNote = profile.FallbackNote,
		}));

		// Prometheus scrapes this directly, on the port the app already serves. A collector in
		// between would be a second process to run before any of this is visible.
		app.MapPrometheusScrapingEndpoint();

		app.Run();
	}

	/// <summary>
	/// Forces the startup dependencies into existence before the server accepts a request, without
	/// otherwise touching the pipeline.
	/// </summary>
	private sealed class StartupDependencies : IStartupFilter
	{
		[SuppressMessage("Major Code Smell", "S1144:Unused private types or members should be removed",
			Justification = "False positive: the constructor is invoked by the DI container, and that invocation is " +
				"the mechanism ordering the database bootstrap before the server accepts a request (SPEC-130) and " +
				"the roster's validation before a turn can run (SPEC-100). It looks unused precisely because " +
				"nothing calls it explicitly.")]
		public StartupDependencies(DatabaseBootstrapResult database, Roster roster)
		{
			_ = database;
			_ = roster;
		}

		public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => next;
	}

	/// <summary>
	/// Stands in for the indexing service when indexing is switched off. Nothing is going to build an
	/// index, so the state is marked ready at startup rather than leaving every query waiting on one.
	/// </summary>
	private sealed class IndexingDisabledService : IHostedService
	{
		private readonly IndexState _indexState;

		public IndexingDisabledService(IndexState indexState)
		{
			this._indexState = indexState;
		}

		public Task StartAsync(CancellationToken cancellationToken)
		{
			this._indexState.MarkReady(DateTime.UtcNow);
			return Task.CompletedTask;
		}

		public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
	}
}
