using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FolderAssistant.Embedding;
using FolderAssistant.Indexing;
using FolderAssistant.Indexing.Scanning;
using FolderAssistant.Indexing.Watching;
using FolderAssistant.Persistence;
using FolderAssistant.Retrieval;
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
	/// For the tests whose subject is the host's wiring rather than the embedder: an in-process profile
	/// with no native dependency, so the test asks nothing of the machine it runs on.
	/// </summary>
	private static readonly (String Key, String Value) InProcessProfile = ($"{AgentConfig.SectionName}:Profile", "lsa-blob");

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


	/// <summary>
	/// The subject is the composition root's wiring of the pass and the state, so the profile is an
	/// in-process one: the default embeds through a local Ollama, which neither this machine nor CI is
	/// promised to have, and a test that depended on it would report the server's absence as a defect here.
	/// </summary>
	[Fact]
	public async Task With_Indexing_Enabled_The_Index_Becomes_Ready_On_Its_Own()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("notes.md"), "alpha beta gamma");

		using HostFixture host = new(folder, InProcessProfile);

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

	/// <summary>
	/// The composition root's wiring of the front end, which nothing below the host can check: the
	/// store, the bridge and the indexer are constructed there, and a registration that resolved the
	/// wrong one would boot and serve while the folder quietly stopped being followed. A file written
	/// after startup is indexed by the running host, with nothing in the test touching any of it.
	/// </summary>
	[Fact]
	public async Task A_File_Written_After_Startup_Is_Indexed_By_The_Running_Host()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("notes.md"), "alpha beta gamma");

		using HostFixture host = new(folder, InProcessProfile, ($"{AgentConfig.SectionName}:Indexing:DebounceMilliseconds", "100"));

		using HttpClient client = host.CreateClient();
		await client.GetAsync("/");

		IIndexState state = host.Services.GetRequiredService<IIndexState>();
		await WaitFor(() => state.Status == IndexStatus.Ready, "the initial index");

		await File.WriteAllTextAsync(folder.Combine("later.md"), "delta epsilon zeta");

		await WaitFor(() => CountIndexedFiles(folder) == 2, "the new file to be recorded");
		await WaitFor(() => CountVectorsOf(host, folder, FileIdentity.For("later.md")) > 0, "the new file to be embedded");

		CountIndexedFiles(folder).Should().Be(2);
		CountVectorsOf(host, folder, FileIdentity.For("later.md")).Should().BeGreaterThan(0);
	}

	/// <summary>
	/// A report is fed into the running front end's own debouncer, so the notifier has to be that
	/// object and not a second one built from the same registrations — which would accept every report
	/// and drain none of them.
	/// </summary>
	[Fact]
	public async Task The_Change_Notifier_Is_The_Running_Front_End_Itself()
	{
		using TempFolder folder = new();
		using HostFixture host = new(folder, ($"{AgentConfig.SectionName}:Indexing:Enabled", "false"));

		using HttpClient client = host.CreateClient();
		await client.GetAsync("/");

		IIndexChangeNotifier notifier = host.Services.GetRequiredService<IIndexChangeNotifier>();
		IFolderIndexer indexer = host.Services.GetRequiredService<IFolderIndexer>();

		notifier.Should().BeSameAs(indexer);
	}

	/// <summary>
	/// The metrics wiring, which no unit test can reach: the meter name has to be the one registered
	/// with the host, and the scrape endpoint has to be mapped. Both are strings agreed on in two
	/// places, and getting either wrong produces an endpoint that serves perfectly well and reports
	/// nothing about retrieval — the failure looks like an idle system.
	///
	/// <para>
	/// A series only exists once its instrument has recorded, so this searches first. Nothing on the
	/// request path does that yet, which is why the search here is made directly.
	/// </para>
	/// </summary>
	[Fact]
	public async Task A_Search_Reaches_The_Scrape_Endpoint_As_A_Tagged_Series()
	{
		using TempFolder folder = new();
		// The subject is the meter, not the embedder: the placeholder profile embeds a query with no fit
		// and no index, where the default's corpus-fitted embedder would refuse an unfitted query.
		using HostFixture host = new(
			folder,
			($"{AgentConfig.SectionName}:Indexing:Enabled", "false"),
			($"{AgentConfig.SectionName}:Profile", "programmable-blob"));

		using HttpClient client = host.CreateClient();
		await client.GetAsync("/");

		IRetrievalQuery query = host.Services.GetRequiredService<IRetrievalQuery>();
		query.Search(folder.Combine(".folderassistant", "manifest.db"), "alpha", new RetrievalOptions(TopK: 3));

		String metrics = await client.GetStringAsync("/metrics");

		metrics.Should().Contain("retrieval_search_count");
		metrics.Should().Contain("retrieval_search_duration");

		// The backend tag is the point of the measurement: the two implementations are a baseline and
		// a candidate, and an untagged series would describe neither.
		metrics.Should().Contain("backend=\"CosineRetrievalQuery\"");
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

	/// <summary>
	/// Counted through the host's own vector store reader, under the host's own active model version, so
	/// the check holds whichever backend the profile composed: the default writes <c>vec0</c> tables, and a
	/// count over <c>chunk_vector</c> would read zero there while the file was embedded.
	/// </summary>
	private static Int64 CountVectorsOf(HostFixture host, TempFolder folder, String fileId)
	{
		String databasePath = folder.Combine(".folderassistant", "manifest.db");
		String modelVersionId = host.Services.GetRequiredService<IVectorizer>().Descriptor.ModelVersionId;
		IVectorStoreReader reader = host.Services.GetRequiredService<IVectorStoreReader>();

		HashSet<String> chunkIds = new(StringComparer.Ordinal);
		using (Microsoft.Data.Sqlite.SqliteConnection connection = new($"Data Source={databasePath}"))
		{
			connection.Open();
			using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
			command.CommandText = "SELECT chunk_id FROM chunk_manifest WHERE file_id = $fileId;";
			command.Parameters.AddWithValue("$fileId", fileId);
			using Microsoft.Data.Sqlite.SqliteDataReader rows = command.ExecuteReader();
			while (rows.Read())
			{
				chunkIds.Add(rows.GetString(0));
			}
		}

		return reader.ReadVectorsByModelVersion(databasePath, modelVersionId)
			.Count(vector => chunkIds.Contains(vector.ChunkId));
	}

	private static async Task WaitFor(Func<Boolean> condition, String what)
	{
		DateTime deadline = DateTime.UtcNow.AddSeconds(20);

		while (DateTime.UtcNow < deadline)
		{
			if (condition())
			{
				return;
			}

			await Task.Delay(25);
		}

		throw new TimeoutException($"Timed out waiting for {what}.");
	}

	/// <summary>
	/// What is running is one request away. Named, the profile is echoed as named; unnamed, it is the
	/// platform's default, and the note is present exactly when the default could not run here and its
	/// blob twin did — the fallback that is allowed because it is never silent.
	/// </summary>
	[Fact]
	public async Task The_Root_Reports_The_Active_Profile_And_Whether_It_Fell_Back()
	{
		using TempFolder named = new();
		using TempFolder unnamed = new();
		using HostFixture namedHost = new(named, ($"{AgentConfig.SectionName}:Profile", "programmable-blob"));
		using HostFixture unnamedHost = new(unnamed);

		FolderResponse? namedResponse = await namedHost.CreateClient().GetFromJsonAsync<FolderResponse>("/");
		FolderResponse? unnamedResponse = await unnamedHost.CreateClient().GetFromJsonAsync<FolderResponse>("/");

		namedResponse!.Profile.Should().Be("programmable-blob");
		namedResponse.ProfileNote.Should().BeNull();
		unnamedResponse!.Profile.Should().Be(SqliteVecExtension.IsAvailable ? "ollama-vec" : "ollama-blob");
		(unnamedResponse.ProfileNote is null).Should().Be(SqliteVecExtension.IsAvailable);
	}

	/// <summary>
	/// The promise the default makes: an Ollama that is not there is a failed index with a message that
	/// says what to do, never a quietly different embedder and never an index that reports building for
	/// ever. Pinned against a port nothing listens on, so it holds on a machine that does run Ollama.
	/// </summary>
	[Fact]
	public async Task An_Unreachable_Ollama_Fails_The_Index_Loudly_Rather_Than_Substituting_An_Embedder()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("notes.md"), "alpha beta gamma");

		using HostFixture host = new(
			folder,
			($"{AgentConfig.SectionName}:Profile", "ollama-blob"),
			($"{AgentConfig.SectionName}:Indexing:OllamaEndpoint", "http://127.0.0.1:9/v1"),
			($"{AgentConfig.SectionName}:Indexing:OllamaTimeoutSeconds", "5"));

		using HttpClient client = host.CreateClient();
		FolderResponse? response = await client.GetFromJsonAsync<FolderResponse>("/");

		IIndexState state = host.Services.GetRequiredService<IIndexState>();
		await WaitFor(() => state.Status != IndexStatus.Building, "the probe to give up");

		response!.Profile.Should().Be("ollama-blob");
		state.Status.Should().Be(IndexStatus.Failed);
		state.Error!.Message.Should().Contain("Ollama").And.Contain("running at the configured endpoint");
	}

	private sealed record FolderResponse(String Name, String AnalyzedFolder, String Profile, String? ProfileNote);

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
