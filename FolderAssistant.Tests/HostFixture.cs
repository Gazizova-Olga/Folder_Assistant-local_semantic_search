using FolderAssistant.Surfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FolderAssistant.Tests;

/// <summary>
/// A host pointed at a temporary folder, with settings injected the way a deployment would add them —
/// after the application has composed itself.
///
/// <para>
/// It lives in its own file rather than inside one test class because more than one class boots the real
/// host: the composition root's own tests, and the endpoints that only exist in a running application.
/// </para>
/// </summary>
internal sealed class HostFixture : WebApplicationFactory<Program>
{
	private readonly TempFolder _folder;
	private readonly (String Key, String Value)[] _settings;

	public HostFixture(TempFolder folder, params (String Key, String Value)[] settings)
	{
		this._folder = folder;
		this._settings = settings;
	}

	/// <summary>Registrations that replace the application's own, for the one seam a test stands in for: the model.</summary>
	public Action<IServiceCollection>? TestServices { get; init; }

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		builder.UseEnvironment(Environments.Production);

		builder.ConfigureAppConfiguration(configuration =>
		{
			Dictionary<String, String?> values = new()
			{
				[$"{AgentConfig.SectionName}:Persistence:AnalyzedFolderPath"] = this._folder.Path,
			};

			foreach ((String key, String value) in this._settings)
			{
				values[key] = value;
			}

			configuration.AddInMemoryCollection(values);
		});

		// No test reads the test process's own standard input: the console is redirected unless a test
		// types at it through its own streams, registered after these so that they win.
		builder.ConfigureTestServices(services =>
			services.AddSingleton(new ConsoleStreams(TextReader.Null, TextWriter.Null, InputRedirected: true)));

		if (this.TestServices is not null)
		{
			builder.ConfigureTestServices(this.TestServices);
		}
	}
}
