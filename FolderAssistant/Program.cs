using System.Diagnostics.CodeAnalysis;
using FolderAssistant.Embedding;
using FolderAssistant.Indexing;
using FolderAssistant.Indexing.Outbox;
using FolderAssistant.Indexing.Scanning;
using FolderAssistant.Indexing.Watching;
using FolderAssistant.Persistence;
using FolderAssistant.Retrieval;
using FolderAssistant.Tools;
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

		builder.Services.AddSingleton<IndexState>();
		builder.Services.AddSingleton<IIndexState>(sp => sp.GetRequiredService<IndexState>());

		// Which implementations run is decided here, by name, from one configuration value. The
		// profile is a bundle rather than a set of independent switches, so a mismatched combination
		// cannot be expressed. Resolve throws on an unknown or platform-unavailable name and never
		// falls back, because a silent fallback would serve answers from a different embedding space
		// than the operator believes they configured.
		builder.Services.AddSingleton(sp =>
		{
			ModuleSet profile = CompositionProfiles.Resolve(sp.GetRequiredService<AgentConfig>().Profile);
			Console.WriteLine($"Composition profile: {profile.Name}");
			return profile;
		});

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
				config.Indexing,
				config.Persistence.MetadataFolderName);
		});

		// The read tools over their own containment guard. Composed and resolved by nothing: the agent
		// that would reflect the holder's methods into tools does not exist yet. The mutation holder,
		// when it exists, gets a second guard over the same root — the split is the contract.
		builder.Services.AddSingleton(sp =>
		{
			AgentConfig config = sp.GetRequiredService<AgentConfig>();

			return new ReadTools(new WorkspacePathGuard(
				config.ResolveAnalyzedFolderPath(),
				config.Persistence.MetadataFolderName));
		});

		// The front end: watcher, reconciler, per-change pipeline and outbox dispatcher, composed by
		// the library and started by the indexing service once the whole-folder pass has succeeded.
		builder.Services.AddSingleton<IFolderIndexer>(sp =>
		{
			AgentConfig config = sp.GetRequiredService<AgentConfig>();
			FolderIndexStore store = sp.GetRequiredService<FolderIndexStore>();

			// The extension list and the size bound are the scanner's own, from one source of truth.
			// Left at the library's defaults, every binary in the folder would be recorded, queued and
			// delivered to a bridge that refuses each one, and an oversize file would be read whole.
			FolderIndexerOptions options = new()
			{
				RootPath = config.ResolveAnalyzedFolderPath(),
				MetadataFolderName = config.Persistence.MetadataFolderName,
				IndexableExtensions = LocalTextFileScanner.IndexableExtensions,
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
				config.Persistence.MetadataFolderName);

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

		app.MapGet("/", (AgentConfig config) => Results.Ok(new
		{
			name = "Folder Assistant",
			analyzedFolder = config.ResolveAnalyzedFolderPath(),
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
				"the mechanism ordering the database bootstrap before the server accepts a request (SPEC-130). It " +
				"looks unused precisely because nothing calls it explicitly.")]
		public StartupDependencies(DatabaseBootstrapResult database)
		{
			_ = database;
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
