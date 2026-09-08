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

		DatabaseBootstrapResult database = new FolderDatabaseBootstrapper()
			.EnsureInitialized(analyzedFolderPath, config.Persistence);

		Console.WriteLine(
			$"Database bootstrap: {(database.Created ? "created" : "reused")} at {database.DatabasePath}");

		WebApplication app = builder.Build();

		app.MapGet("/", () => Results.Ok(new
		{
			name = "Folder Assistant",
			analyzedFolder = analyzedFolderPath,
		}));

		app.Run();
	}
}
