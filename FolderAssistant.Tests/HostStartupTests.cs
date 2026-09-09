using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FolderAssistant.Indexing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FolderAssistant.Tests;

/// <summary>
/// The composition root, run as the host actually runs it.
///
/// <para>
/// These exist because the property they cover cannot be checked any other way. Configuration is
/// bound through <c>IOptions</c> rather than read off the builder, and the only thing that
/// distinguishes the two is whether a source added after composition is honoured — which needs a
/// host that adds one.
/// </para>
/// </summary>
public sealed class HostStartupTests
{
	/// <summary>
	/// The reason the binding is lazy. An eager bind freezes the values while the composition root
	/// runs, so a source added afterwards is discarded without a word — and the host quietly serves
	/// whatever the developer's own settings said. Reverting to an eager bind fails this test.
	/// </summary>
	[Fact]
	public async Task Configuration_Supplied_After_Composition_Reaches_The_Running_App()
	{
		using TempFolder folder = new();
		using HostFixture host = new(folder);

		FolderResponse? response = await host.CreateClient().GetFromJsonAsync<FolderResponse>("/");

		response.Should().NotBeNull();
		response!.AnalyzedFolder.Should().Be(Path.GetFullPath(folder.Path));
	}

	/// <summary>
	/// The bootstrap is a startup dependency, not something the first request triggers. If it were
	/// ordered after the server starts listening, a request arriving early would find no database.
	/// </summary>
	[Fact]
	public async Task The_Database_Is_Bootstrapped_Before_Any_Request_Is_Served()
	{
		using TempFolder folder = new();
		using HostFixture host = new(folder);

		HttpResponseMessage response = await host.CreateClient().GetAsync("/");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		File.Exists(folder.Combine(".folderassistant", "manifest.db")).Should().BeTrue();
	}

	/// <summary>
	/// <summary>
	/// With indexing off, nothing will ever build an index, so retrieval must not sit waiting for
	/// one. Whether indexing runs at all is decided when the hosted-service factory executes — which
	/// is only after the configuration is final, and so only works because the binding is lazy.
	///
	/// <para>
	/// Asserting `Ready` alone would not discriminate: an ignored override leaves indexing enabled,
	/// and a pass that ran and finished also reports `Ready`. The empty manifest is what separates
	/// "nothing will be indexed" from "something already was".
	/// </para>
	/// </summary>
	[Fact]
	public async Task Disabling_Indexing_Leaves_The_Index_Ready_Without_Indexing_Anything()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("notes.md"), "alpha beta gamma");

		using HostFixture host = new(folder, ($"{AgentConfig.SectionName}:Indexing:Enabled", "false"));

		// Forces the host to start; the services are not built until something asks.
		using HttpClient client = host.CreateClient();
		await client.GetAsync("/");

		IIndexState state = host.Services.GetRequiredService<IIndexState>();

		state.Status.Should().Be(IndexStatus.Ready);
		CountIndexedFiles(folder).Should().Be(0);
	}


	[Fact]
	public async Task With_Indexing_Enabled_The_Index_Becomes_Ready_On_Its_Own()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("notes.md"), "alpha beta gamma");

		using HostFixture host = new(folder);

		using HttpClient client = host.CreateClient();
		await client.GetAsync("/");

		IIndexState state = host.Services.GetRequiredService<IIndexState>();

		DateTime deadline = DateTime.UtcNow.AddSeconds(15);
		while (state.Status == IndexStatus.Building && DateTime.UtcNow < deadline)
		{
			await Task.Delay(25);
		}

		state.Status.Should().Be(IndexStatus.Ready);
	}

	private static Int64 CountIndexedFiles(TempFolder folder)
	{
		String databasePath = folder.Combine(".folderassistant", "manifest.db");

		using Microsoft.Data.Sqlite.SqliteConnection connection = new($"Data Source={databasePath}");
		connection.Open();

		using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
		command.CommandText = "SELECT COUNT(*) FROM file_manifest;";

		return (Int64)(command.ExecuteScalar() ?? 0L);
	}

	private sealed record FolderResponse(String Name, String AnalyzedFolder);

	/// <summary>
	/// A host pointed at a temporary folder, with settings injected the way a deployment would add
	/// them — after the application has composed itself.
	/// </summary>
	private sealed class HostFixture : WebApplicationFactory<Program>
	{
		private readonly TempFolder _folder;
		private readonly (String Key, String Value)[] _settings;

		public HostFixture(TempFolder folder, params (String Key, String Value)[] settings)
		{
			this._folder = folder;
			this._settings = settings;
		}

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
		}
	}
}
