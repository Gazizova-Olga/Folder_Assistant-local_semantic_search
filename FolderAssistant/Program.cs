using FolderAssistant.Embedding;
using FolderAssistant.Indexing;
using FolderAssistant.Persistence;
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

			// Composition root for the embedding provider: swapping the IVectorizer built here is the
			// only change needed to index with a different backend.
			IVectorizer vectorizer = new ProgrammableEmbeddingVectorizer(
				config.Indexing.ModelVersionId, config.Indexing.VectorDimension);

			FolderIndexingPipeline pipeline = new(vectorizer);

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
