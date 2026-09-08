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

		WebApplication app = builder.Build();

		app.MapGet("/", static () => Results.Ok(new { name = "Folder Assistant" }));

		app.Run();
	}
}
