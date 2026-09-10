using FolderAssistant.Embedding;
using FolderAssistant.Indexing;
using FolderAssistant.Persistence;
using FolderAssistant.Retrieval;
using Microsoft.Extensions.Options;

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
		builder.Services.AddSingleton(sp => sp.GetRequiredService<ModuleSet>()
			.CreateVectorizer(sp.GetRequiredService<AgentConfig>().Indexing));

		builder.Services.AddSingleton(sp => sp.GetRequiredService<ModuleSet>().CreateVectorStoreWriter());
		builder.Services.AddSingleton(sp => sp.GetRequiredService<ModuleSet>().CreateVectorStoreReader());

		// Retrieval was implemented and tested but composed nowhere, which SPEC-000 called the
		// central open item. This registers it, so it is resolvable from the container. The gap that
		// remains is that nothing on the request path asks it anything yet.
		builder.Services.AddSingleton(sp => sp.GetRequiredService<ModuleSet>().CreateRetrievalQuery(
			sp.GetRequiredService<IVectorizer>(),
			sp.GetRequiredService<IVectorStoreReader>(),
			sp.GetRequiredService<IIndexState>()));

		builder.Services.AddSingleton(sp =>
		{
			AgentConfig config = sp.GetRequiredService<AgentConfig>();

			DatabaseBootstrapResult database = new FolderDatabaseBootstrapper()
				.EnsureInitialized(config.ResolveAnalyzedFolderPath(), config.Persistence);

			Console.WriteLine(
				$"Database bootstrap: {(database.Created ? "created" : "reused")} at {database.DatabasePath}");

			return database;
		});

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
				sp.GetRequiredService<IVectorStoreReader>());

			IFileChangeFeed? changeFeed = config.Indexing.WatchEnabled
				? new FileSystemWatcherChangeFeed(
					analyzedFolderPath,
					config.Persistence.MetadataFolderName,
					TimeSpan.FromMilliseconds(config.Indexing.DebounceMilliseconds),
					TimeSpan.FromSeconds(config.Indexing.ReconciliationIntervalSeconds))
				: null;

			return new FolderIndexingService(
				() => pipeline.Run(analyzedFolderPath, databasePath, config.Indexing),
				changeFeed,
				indexState);
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

		app.Run();
	}

	/// <summary>
	/// Forces the startup dependencies into existence before the server accepts a request, without
	/// otherwise touching the pipeline.
	/// </summary>
	private sealed class StartupDependencies : IStartupFilter
	{
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
