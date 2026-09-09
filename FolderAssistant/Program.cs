using FolderAssistant.Embedding;
using FolderAssistant.Indexing;
using FolderAssistant.Persistence;

namespace FolderAssistant;

internal sealed class Program
{
	// Never instantiated. It exists as a type only so a test host can name it as its entry point.
	private Program()
	{
	}

	private static void Main(String[] args)
	{
		WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

		AgentConfig config = new();
		builder.Configuration.GetSection(AgentConfig.SectionName).Bind(config);

		builder.WebHost.UseUrls($"http://localhost:{config.Port}");

		String analyzedFolderPath = config.ResolveAnalyzedFolderPath();

		FolderDatabaseBootstrapper bootstrap = new();
		DatabaseBootstrapResult database = bootstrap.EnsureInitialized(analyzedFolderPath, config.Persistence);

		Console.WriteLine(
			$"Database bootstrap: {(database.Created ? "created" : "reused")} at {database.DatabasePath}");

		// Retrieval consults this rather than answering from an index that is still filling up.
		IndexState indexState = new();
		builder.Services.AddSingleton<IIndexState>(indexState);

		if (config.Indexing.Enabled)
		{
			// Composition root for the embedding provider: swapping the IVectorizer built here is the
			// only change needed to index with a different backend.
			IVectorizer vectorizer = new ProgrammableEmbeddingVectorizer(
				config.Indexing.ModelVersionId, config.Indexing.VectorDimension);

			FolderIndexingPipeline pipeline = new(vectorizer);

			builder.Services.AddSingleton<IHostedService>(_ => new FolderIndexingService(
				() => pipeline.Run(analyzedFolderPath, database.DatabasePath, config.Indexing),
				indexState));
		}
		else
		{
			// Nothing is going to build an index, so a query must not sit waiting for one. Ready with
			// no vectors returns nothing, which is the honest answer here.
			indexState.MarkReady(DateTime.UtcNow);
		}

		WebApplication app = builder.Build();

		app.MapGet("/", () => Results.Ok(new
		{
			name = "Folder Assistant",
			analyzedFolder = analyzedFolderPath,
		}));

		app.Run();
	}
}
