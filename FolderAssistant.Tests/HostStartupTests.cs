using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FolderAssistant.Agents;
using FolderAssistant.Embedding;
using FolderAssistant.Indexing;
using FolderAssistant.Indexing.Scanning;
using FolderAssistant.Indexing.Watching;
using FolderAssistant.Persistence;
using FolderAssistant.Retrieval;
using FolderAssistant.Surfaces;
using FolderAssistant.Tests.Agents;
using FolderAssistant.Tests.Tools;
using FolderAssistant.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.AI;

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
	/// The conversation database is bootstrapped on the same terms and as a separate file (SPEC-170).
	/// It is ordered before the server listens so that the store which will replace the in-process
	/// session store finds its schema there, rather than creating it on the request path.
	/// </summary>
	[Fact]
	public async Task The_Conversation_Database_Is_A_Second_File_Bootstrapped_Before_Any_Request_Is_Served()
	{
		using TempFolder folder = new();
		using HostFixture host = new(folder);

		HttpResponseMessage response = await host.CreateClient().GetAsync("/");

		response.StatusCode.Should().Be(HttpStatusCode.OK);
		File.Exists(folder.Combine(".folderassistant", "conversations.db")).Should().BeTrue();
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
		namedResponse.EmbeddingEndpointNote.Should().BeNull();
		unnamedResponse!.Profile.Should().Be(SqliteVecExtension.IsAvailable ? "ollama-vec" : "ollama-blob");
		(unnamedResponse.ProfileNote is null).Should().Be(SqliteVecExtension.IsAvailable);
		unnamedResponse.EmbeddingEndpointNote.Should().BeNull();
	}

	/// <summary>
	/// The promise the whole design rests on, enforced where an operator cannot walk past it: document
	/// text goes to the embedding endpoint, so an endpoint off this machine stops the host at startup
	/// rather than failing the first index half a minute later, where it reads as an unreachable server.
	/// The refusal names the setting to change and the setting that makes the trade deliberate.
	/// </summary>
	[Fact]
	public void A_Remote_Embedding_Endpoint_Stops_The_Host_Before_It_Listens()
	{
		using TempFolder folder = new();
		using HostFixture remoteHost = new(
			folder,
			($"{AgentConfig.SectionName}:Profile", "ollama-blob"),
			($"{AgentConfig.SectionName}:Indexing:Enabled", "false"),
			($"{AgentConfig.SectionName}:Indexing:OllamaEndpoint", "http://gpu.example.com:11434/v1"));

		Action boot = () => remoteHost.CreateClient();

		boot.Should().Throw<Exception>()
			.Which.ToString().Should()
				.Contain("Indexing:OllamaEndpoint").And
				.Contain("Indexing:AllowRemoteEmbeddingEndpoint");
	}

	/// <summary>
	/// And with the opt-out the host runs — reporting it where the profile fallback is reported, for the
	/// same reason: a system running something other than what its documentation promises has to say so
	/// somewhere a person can look, not only in a startup line that has scrolled away.
	/// </summary>
	[Fact]
	public async Task The_Root_Reports_A_Remote_Embedding_Endpoint_The_Operator_Allowed()
	{
		using TempFolder folder = new();
		using HostFixture host = new(
			folder,
			($"{AgentConfig.SectionName}:Profile", "ollama-blob"),
			($"{AgentConfig.SectionName}:Indexing:Enabled", "false"),
			($"{AgentConfig.SectionName}:Indexing:OllamaEndpoint", "http://gpu.example.com:11434/v1"),
			($"{AgentConfig.SectionName}:Indexing:AllowRemoteEmbeddingEndpoint", "true"));

		FolderResponse? response = await host.CreateClient().GetFromJsonAsync<FolderResponse>("/");

		response!.EmbeddingEndpointNote.Should().NotBeNull()
			.And.Subject.As<String>().Should().Contain("gpu.example.com");
	}

	/// <summary>
	/// The search holder's wiring, which no unit test can reach: it has to be built over the wrapped query
	/// the root composed and the database the root bootstrapped, or it would search a different index than
	/// the one the folder was indexed into — and answer plausibly from nothing.
	/// </summary>
	[Fact]
	public async Task The_Search_Tools_Answer_Through_The_Composed_Query_Over_The_Indexed_Folder()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("notes.md"), "alpha beta gamma delta epsilon");
		await File.WriteAllTextAsync(folder.Combine("other.md"), "zeta eta theta iota kappa");

		using HostFixture host = new(folder, InProcessProfile);

		using HttpClient client = host.CreateClient();
		await client.GetAsync("/");

		IIndexState state = host.Services.GetRequiredService<IIndexState>();
		await WaitFor(() => state.Status == IndexStatus.Ready, "the initial index");

		FilesAbout about = host.Services.GetRequiredService<SearchTools>().FindFilesAbout("alpha beta", maxFiles: 2);

		about.Files.Should().NotBeEmpty();
		about.Files.Select(file => file.Path).Should().Contain("notes.md");
		about.Files.Should().OnlyContain(file => file.Path == "notes.md" || file.Path == "other.md");
	}

	/// <summary>
	/// Snippet verification end to end, which no unit test can reach: the hash the real pipeline recorded
	/// for a chunk — over the extractor's decode and the chunker's join — is the hash the composed builder
	/// recomputes from the file, so an unchanged file rebuilds verified; and a hit from before an edit
	/// yields no text afterwards. The second half holds whether or not the front end has re-indexed the
	/// edit by then: the old hit's hash is the old text's, and the window now holds different text.
	/// </summary>
	[Fact]
	public async Task A_Passage_Rebuilt_Through_The_Composed_Builder_Verifies_Against_The_Indexs_Own_Hash()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("notes.md"), "alpha beta gamma delta epsilon zeta");

		using HostFixture host = new(folder, InProcessProfile);

		using HttpClient client = host.CreateClient();
		await client.GetAsync("/");

		IIndexState state = host.Services.GetRequiredService<IIndexState>();
		await WaitFor(() => state.Status == IndexStatus.Ready, "the initial index");

		IReadOnlyList<RetrievalHit> hits = host.Services.GetRequiredService<IRetrievalQuery>()
			.Search(folder.Combine(".folderassistant", "manifest.db"), "alpha beta", new RetrievalOptions(TopK: 3));
		PassageBuilder builder = host.Services.GetRequiredService<PassageBuilder>();

		IReadOnlyList<RebuiltPassage> before = builder.Rebuild(hits);
		await File.WriteAllTextAsync(folder.Combine("notes.md"), "alpha beta gamma delta EPSILON zeta");
		IReadOnlyList<RebuiltPassage> after = builder.Rebuild(hits);

		hits.Should().NotBeEmpty();
		hits.Should().OnlyContain(hit => hit.ChunkHash.Length == 64);
		before.Should().OnlyContain(passage => passage.State == PassageState.Verified);
		before[0].Text.Should().Be("alpha beta gamma delta epsilon zeta");
		after.Should().OnlyContain(passage => passage.State == PassageState.Stale && passage.Text == null);
	}

	/// <summary>
	/// The passage search through the real root and a real turn: the coordinator calls <c>SearchIndex</c>
	/// over the indexed folder and is handed verified text; and the turn's memo holds — two identical calls
	/// in one turn cost one retrieval, the same call in the next turn costs another — which only the
	/// execution opening the scope around the framework's loop can make true.
	/// </summary>
	[Fact]
	public async Task A_Turn_Searches_The_Index_Through_The_Composed_Tool_And_The_Memo_Holds_For_The_Turn()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("notes.md"), "alpha beta gamma delta epsilon zeta");
		RecordingRetrievalTelemetry retrievals = new();
		ScriptedChatClient model = new(
			ScriptedChatClient.Call("SearchIndex", new() { ["query"] = "alpha beta", ["maxResults"] = 3 }),
			ScriptedChatClient.Call("SearchIndex", new() { ["query"] = "alpha beta", ["maxResults"] = 3 }),
			ScriptedChatClient.Text("Found it."),
			ScriptedChatClient.Call("SearchIndex", new() { ["query"] = "alpha beta", ["maxResults"] = 3 }),
			ScriptedChatClient.Text("Found it again."));
		using HostFixture host = new(folder, InProcessProfile)
		{
			TestServices = services =>
			{
				services.AddSingleton<Func<ProviderConfig, IChatClient>>(_ => _ => model);
				services.AddSingleton<IRetrievalTelemetry>(retrievals);
			},
		};

		using HttpClient client = host.CreateClient();
		await client.GetAsync("/");
		IIndexState state = host.Services.GetRequiredService<IIndexState>();
		await WaitFor(() => state.Status == IndexStatus.Ready, "the initial index");
		WorkflowRunner runner = host.Services.GetRequiredService<WorkflowRunner>();

		ChatResponse first = await runner.GetResponseAsync([new ChatMessage(ChatRole.User, "what is in the notes?")]);
		Int32 afterFirstTurn = retrievals.Calls.Count;
		await runner.GetResponseAsync([new ChatMessage(ChatRole.User, "again?")], new ChatOptions { ConversationId = first.ConversationId });

		first.Text.Should().Be("Found it.");
		String toolResult = model.Calls[1].Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Single().Result!.ToString()!;
		toolResult.Should().Contain("alpha beta gamma delta epsilon zeta").And.Contain("notes.md");
		afterFirstTurn.Should().Be(1);
		retrievals.Calls.Should().HaveCount(2);
	}

	/// <summary>
	/// The batch hold at the agent-run boundary, through the real root: a turn that writes two files reports
	/// both to the front end while one hold is open, and the hold is released when the turn ends — so the
	/// index sees the edit as one batch and not one pass per step. The notifier is replaced to observe it;
	/// everything else is the application's.
	///
	/// <para>
	/// The mutation is granted through a configured roster, because the shipped default grants none — which
	/// makes this also the one host test that runs a turn through an operator's own `Workflow:Agents`.
	/// </para>
	/// </summary>
	[Fact]
	public async Task A_Turn_Holds_The_Index_Batch_Around_Every_File_It_Changes()
	{
		using TempFolder folder = new();
		RecordingNotifier indexer = new();
		ScriptedChatClient model = new(
			ScriptedChatClient.Call("Create", new() { ["path"] = "a.md", ["content"] = "alpha" }),
			ScriptedChatClient.Call("Create", new() { ["path"] = "b.md", ["content"] = "beta" }),
			ScriptedChatClient.Text("Both written."));
		using HostFixture host = new(
			folder,
			($"{AgentConfig.SectionName}:Indexing:Enabled", "false"),
			($"{AgentConfig.SectionName}:Workflow:Agents:0:Name", "writer"),
			($"{AgentConfig.SectionName}:Workflow:Agents:0:Description", "writes files in the folder"),
			($"{AgentConfig.SectionName}:Workflow:Agents:0:Tools:0", "Create"))
		{
			TestServices = services =>
			{
				services.AddSingleton<Func<ProviderConfig, IChatClient>>(_ => _ => model);
				services.AddSingleton<IIndexChangeNotifier>(indexer);
			},
		};

		using HttpClient client = host.CreateClient();
		await client.GetAsync("/");
		WorkflowRunner runner = host.Services.GetRequiredService<WorkflowRunner>();

		ChatResponse response = await runner.GetResponseAsync([new ChatMessage(ChatRole.User, "write two notes")]);

		response.Text.Should().Be("Both written.");
		indexer.Reports.Select(report => Path.GetFileName(report.Path)).Should().Equal("a.md", "b.md");
		indexer.HoldsAtReport.Should().Equal(1, 1);
		indexer.HoldsBegun.Should().Be(1);
		indexer.OpenHolds.Should().Be(0);
	}

	private sealed class RecordingRetrievalTelemetry : IRetrievalTelemetry
	{
		public List<RetrievalCallTelemetry> Calls { get; } = [];

		public void Record(RetrievalCallTelemetry call) => this.Calls.Add(call);
	}

	/// <summary>
	/// The mutation holder's wiring: it has to be built over the analyzed folder — a guard over any other
	/// root would write somewhere the index never looks — and refuse the index's own folder, and it has to
	/// be a holder of its own, resolvable apart from the read tools, or a roster could not grant one
	/// without the other.
	/// </summary>
	[Fact]
	public async Task The_Mutation_Tools_Write_Into_The_Analyzed_Folder_Through_Their_Own_Guard()
	{
		using TempFolder folder = new();
		using HostFixture host = new(folder, ($"{AgentConfig.SectionName}:Indexing:Enabled", "false"));

		using HttpClient client = host.CreateClient();
		await client.GetAsync("/");

		MutationTools tools = host.Services.GetRequiredService<MutationTools>();
		FileCreated created = await tools.Create("written.md", "by the tool");
		Func<Task> metadata = () => tools.Create(Path.Combine(".folderassistant", "x.md"), "no");

		created.Path.Should().Be("written.md");
		(await File.ReadAllTextAsync(folder.Combine("written.md"))).Should().Be("by the tool");
		await metadata.Should().ThrowAsync<WorkspaceContainmentException>();
		host.Services.GetRequiredService<ReadTools>().Should().NotBeSameAs(tools);
	}

	/// <summary>
	/// The agent's wiring: the handle resolves from the real root over the configured provider without
	/// touching the network — a client is built, not connected — carries the configured name, and holds
	/// every tool the three holders have, each under its group's facade with the search holder's alone under
	/// the fatal one. A
	/// configuration that cannot name a provider fails when the handle is asked for, with the sentence
	/// that says what to set, never at boot: the host has to come up for the index whether or not a
	/// model is reachable.
	/// </summary>
	[Fact]
	public async Task The_Agent_Resolves_Over_The_Configured_Provider_And_A_Missing_Key_Is_Said_When_Asked_For()
	{
		using TempFolder local = new();
		using TempFolder keyless = new();
		using HostFixture localHost = new(
			local,
			($"{AgentConfig.SectionName}:Indexing:Enabled", "false"),
			($"{AgentConfig.SectionName}:Provider:Type", "OpenAI"),
			($"{AgentConfig.SectionName}:Provider:Endpoint", "http://127.0.0.1:9/v1"),
			($"{AgentConfig.SectionName}:Provider:DeploymentName", "local-model"),
			($"{AgentConfig.SectionName}:AgentName", "Archivist"));
		using HostFixture keylessHost = new(
			keyless,
			($"{AgentConfig.SectionName}:Indexing:Enabled", "false"),
			($"{AgentConfig.SectionName}:Provider:Type", "OpenAI"));

		await localHost.CreateClient().GetAsync("/");
		await keylessHost.CreateClient().GetAsync("/");

		AgentHandle handle = localHost.Services.GetRequiredService<StaticWorkflowRoute>().Coordinator;
		Action keylessResolve = () => keylessHost.Services.GetRequiredService<AgentRegistry>();

		handle.Name.Should().Be("Archivist");
		handle.Agent.Name.Should().Be("Archivist");
		IReadOnlyList<AITool> tools = handle.Tools;

		// The shipped default, through the real host: the read and search tools, and not one mutation.
		tools.Select(tool => tool.Name).Should().BeEquivalentTo(
			"InspectDirectory", "ReadFile", "Retrieve", "FindFiles", "SearchText", "SearchIndex", "FindFilesAbout");
		tools.Select(tool => tool.Name).Should().NotIntersectWith(["Create", "Update", "ReplaceLines", "Delete"]);
		tools.OfType<ToolFacade>().Where(tool => tool.Group == ToolGroup.Search).Select(tool => tool.Name).Should().Equal("SearchIndex", "FindFilesAbout");
		tools.OfType<ToolFacade>().Should().HaveCount(tools.Count);
		keylessResolve.Should().Throw<InvalidOperationException>().WithMessage("*Provider:ApiKey*");
	}

	/// <summary>
	/// The roster from the real root: asked for, the default roster is three agents with the orchestrator
	/// holding nothing but its two delegations and every turn entering it; and a roster whose delegation
	/// forms a cycle stops the host before it listens, because nothing bounds delegation at runtime.
	/// </summary>
	[Fact]
	public async Task The_Default_Roster_Resolves_From_The_Root_And_A_Cyclic_Roster_Fails_At_Boot()
	{
		using TempFolder folder = new();
		using TempFolder cyclic = new();
		using HostFixture host = new(
			folder,
			($"{AgentConfig.SectionName}:Indexing:Enabled", "false"),
			($"{AgentConfig.SectionName}:Provider:Type", "OpenAI"),
			($"{AgentConfig.SectionName}:Provider:Endpoint", "http://127.0.0.1:9/v1"),
			($"{AgentConfig.SectionName}:Provider:DeploymentName", "local-model"),
			($"{AgentConfig.SectionName}:Workflow:UseDefaultRoster", "true"),
			($"{AgentConfig.SectionName}:Workflow:Coordinator", "orchestrator"));
		using HostFixture cyclicHost = new(
			cyclic,
			($"{AgentConfig.SectionName}:Indexing:Enabled", "false"),
			($"{AgentConfig.SectionName}:Workflow:Coordinator", "a"),
			($"{AgentConfig.SectionName}:Workflow:Agents:0:Name", "a"),
			($"{AgentConfig.SectionName}:Workflow:Agents:0:Description", "the a"),
			($"{AgentConfig.SectionName}:Workflow:Agents:0:Delegates:0", "b"),
			($"{AgentConfig.SectionName}:Workflow:Agents:1:Name", "b"),
			($"{AgentConfig.SectionName}:Workflow:Agents:1:Description", "the b"),
			($"{AgentConfig.SectionName}:Workflow:Agents:1:Delegates:0", "a"));

		await host.CreateClient().GetAsync("/");
		AgentRegistry registry = host.Services.GetRequiredService<AgentRegistry>();
		Action boot = () => cyclicHost.CreateClient();

		registry.Handles.Select(handle => handle.Name).Should().Equal("orchestrator", "reader", "mutator");
		registry.Get("orchestrator").Tools.Select(tool => tool.Name).Should().Equal("delegate_to_reader", "delegate_to_mutator");
		registry.Get("reader").Tools.Select(tool => tool.Name).Should().Contain("FindFilesAbout").And.NotContain("Delete");
		registry.Get("mutator").Tools.Select(tool => tool.Name).Should().Contain("Delete").And.NotContain("FindFilesAbout");
		host.Services.GetRequiredService<StaticWorkflowRoute>().Coordinator.Name.Should().Be("orchestrator");
		boot.Should().Throw<Exception>().Which.ToString().Should().Contain("cycle: a -> b -> a");
	}

	/// <summary>
	/// A turn through the real root, with the one seam replaced that a model would sit behind: the runner
	/// resolves, the coordinator calls a real tool over the analyzed folder and reads its result, the next
	/// turn of the conversation remembers the first, and the turn reaches the scrape endpoint as a series.
	/// </summary>
	[Fact]
	public async Task A_Turn_Runs_Through_The_Composed_Runner_Over_The_Real_Tools()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("notes.md"), "alpha beta gamma");
		ScriptedChatClient model = new(
			ScriptedChatClient.Call("ReadFile", new() { ["path"] = "notes.md" }),
			ScriptedChatClient.Text("It holds three words."),
			ScriptedChatClient.Text("The first is alpha."));
		using HostFixture host = new(folder, ($"{AgentConfig.SectionName}:Indexing:Enabled", "false"))
		{
			TestServices = services => services.AddSingleton<Func<ProviderConfig, IChatClient>>(_ => _ => model),
		};

		using HttpClient client = host.CreateClient();
		await client.GetAsync("/");
		WorkflowRunner runner = host.Services.GetRequiredService<WorkflowRunner>();

		ChatResponse first = await runner.GetResponseAsync([new ChatMessage(ChatRole.User, "what is in notes.md?")]);
		ChatResponse second = await runner.GetResponseAsync(
			[new ChatMessage(ChatRole.User, "which is first?")],
			new ChatOptions { ConversationId = first.ConversationId });
		String metrics = await client.GetStringAsync("/metrics");

		first.Text.Should().Be("It holds three words.");
		model.Calls[1].Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Single()
			.Result!.ToString().Should().Contain("alpha beta gamma");
		second.Text.Should().Be("The first is alpha.");
		model.Calls[2].Messages.Where(message => message.Role == ChatRole.User).Select(message => message.Text)
			.Should().Equal("what is in notes.md?", "which is first?");
		metrics.Should().Contain("agent_turn_count").And.Contain("agent_turn_duration");
		metrics.Should().Contain("status=\"Success\"").And.Contain("streamed=\"false\"");
	}

	/// <summary>
	/// The promise the default makes: an Ollama that is not there is a failed index with a message that
	/// says what to do, never a quietly different embedder and never an index that reports building for
	/// ever. Pinned against a port nothing listens on, so it holds on a machine that does run Ollama.
	///
	/// <para>
	/// The deadline is left at its default of 120 s, and that is the second property: a refused port costs
	/// the probe one connection per attempt, not its deadline, because the client retries nothing
	/// underneath the call, so three attempts a second apart give up in a few seconds — 8 s here on
	/// 2026-09-24, where Windows refuses a loopback connect after about two seconds. With the SDK's own
	/// retries underneath, each attempt cost the whole deadline: at five seconds the test took 17 s against
	/// the 20 s wait and lost on a slow CI runner (2026-09-23), and at the default it would not have ended
	/// inside the wait at all.
	/// </para>
	/// </summary>
	[Fact]
	public async Task An_Unreachable_Ollama_Fails_The_Index_Loudly_Rather_Than_Substituting_An_Embedder()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("notes.md"), "alpha beta gamma");

		Stopwatch clock = Stopwatch.StartNew();
		using HostFixture host = new(
			folder,
			($"{AgentConfig.SectionName}:Profile", "ollama-blob"),
			($"{AgentConfig.SectionName}:Indexing:OllamaEndpoint", "http://127.0.0.1:9/v1"));

		using HttpClient client = host.CreateClient();
		FolderResponse? response = await client.GetFromJsonAsync<FolderResponse>("/");

		IIndexState state = host.Services.GetRequiredService<IIndexState>();
		await WaitFor(() => state.Status != IndexStatus.Building, "the probe to give up");
		clock.Stop();

		response!.Profile.Should().Be("ollama-blob");
		state.Status.Should().Be(IndexStatus.Failed);
		state.Error!.Message.Should().Contain("Ollama").And.Contain("running at the configured endpoint");
		clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), "a refused port costs a probe attempt one connection, not the 120 s deadline");
	}

	/// <summary>
	/// The first front end, through the real host: a question typed at the console reaches the composed
	/// runner and the model's answer is written back, and <c>exit</c> stops the host — the console and the
	/// web host are one process, and the person leaving ends it.
	/// </summary>
	[Fact]
	public async Task A_Question_Typed_At_The_Console_Is_Answered_And_Exit_Stops_The_Host()
	{
		using TempFolder folder = new();
		ScriptedChatClient model = new(ScriptedChatClient.Text("Three words."));
		StringWriter console = new();
		using TypedLines keyboard = new();
		using HostFixture host = new(folder, ($"{AgentConfig.SectionName}:Indexing:Enabled", "false"))
		{
			TestServices = services =>
			{
				services.AddSingleton<Func<ProviderConfig, IChatClient>>(_ => _ => model);
				services.AddSingleton(new ConsoleStreams(keyboard, console, InputRedirected: false));
			},
		};

		using HttpClient client = host.CreateClient();
		IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

		keyboard.Type("what is in notes.md?");
		await WaitFor(() => console.ToString().Contains("Three words.", StringComparison.Ordinal), "the answer to be written");
		(await client.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.OK, "the host serves while the console is in use");
		keyboard.Type("exit");
		await WaitFor(() => lifetime.ApplicationStopping.IsCancellationRequested, "exit to stop the host");

		model.Calls.Should().ContainSingle().Which.Messages.Should().Contain(message => message.Role == ChatRole.User && message.Text == "what is in notes.md?");
		console.ToString().Should().Contain(ConsoleChatLoop.Banner(Path.GetFullPath(folder.Path)));
	}

	/// <summary>
	/// The keyless host boots and serves its index, and the refusal of a configuration naming no provider
	/// reaches the person at the prompt, on their first question, naming the setting — not the host at
	/// boot, which the console taking the runner at construction would have caused. The host is still up
	/// to answer the second line, and stops on it.
	/// </summary>
	[Fact]
	public async Task A_Question_With_No_Provider_Configured_Is_Answered_With_The_Missing_Setting_And_The_Host_Serves_On()
	{
		using TempFolder folder = new();
		StringWriter console = new();
		using TypedLines keyboard = new();
		using HostFixture host = new(folder, ($"{AgentConfig.SectionName}:Indexing:Enabled", "false"))
		{
			TestServices = services =>
				services.AddSingleton(new ConsoleStreams(keyboard, console, InputRedirected: false)),
		};

		using HttpClient client = host.CreateClient();
		IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

		keyboard.Type("what is here?");
		await WaitFor(() => console.ToString().Contains(MicrosoftAgentExecution.FailurePrefix, StringComparison.Ordinal), "the refusal to be written");
		(await client.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.OK, "a keyless host serves its index");
		keyboard.Type("exit");
		await WaitFor(() => lifetime.ApplicationStopping.IsCancellationRequested, "exit to stop the host");

		console.ToString().Should().Contain(MicrosoftAgentExecution.FailurePrefix + "Provider:ApiKey is required");
	}

	/// <summary>
	/// A headless run must not stop the host: over a redirected standard input the loop is not started,
	/// so the <c>exit</c> waiting in that input is never read and the host serves on. The wait is a window
	/// in which a loop that ignored the flag would have read the line and stopped the host within
	/// milliseconds, as the test above shows it does.
	/// </summary>
	[Fact]
	public async Task A_Redirected_Standard_Input_Does_Not_Start_The_Console_And_The_Host_Serves_On()
	{
		using TempFolder folder = new();
		StringWriter console = new();
		using HostFixture host = new(folder, ($"{AgentConfig.SectionName}:Indexing:Enabled", "false"))
		{
			TestServices = services =>
				services.AddSingleton(new ConsoleStreams(new StringReader("exit\n"), console, InputRedirected: true)),
		};

		using HttpClient client = host.CreateClient();
		await client.GetAsync("/");
		await Task.Delay(500);

		IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
		lifetime.ApplicationStopping.IsCancellationRequested.Should().BeFalse();
		console.ToString().Should().BeEmpty("the loop was not started, so not even the banner was written");
		(await client.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.OK);
	}

	/// <summary>
	/// The other half of the same rule: a standard input that ends — a closed pipe, Ctrl+Z — ends the loop
	/// and nothing else. The banner proves the loop ran; the host still answers afterwards.
	/// </summary>
	[Fact]
	public async Task A_Closed_Standard_Input_Ends_The_Console_And_The_Host_Serves_On()
	{
		using TempFolder folder = new();
		StringWriter console = new();
		using HostFixture host = new(folder, ($"{AgentConfig.SectionName}:Indexing:Enabled", "false"))
		{
			TestServices = services =>
				services.AddSingleton(new ConsoleStreams(new StringReader(""), console, InputRedirected: false)),
		};

		using HttpClient client = host.CreateClient();
		await client.GetAsync("/");
		await WaitFor(() => console.ToString().Contains(ConsoleChatLoop.Prompt, StringComparison.Ordinal), "the loop to run to the end of its input");
		await Task.Delay(200);

		IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
		lifetime.ApplicationStopping.IsCancellationRequested.Should().BeFalse();
		(await client.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.OK);
	}

	private sealed record FolderResponse(
		String Name,
		String AnalyzedFolder,
		String Profile,
		String? ProfileNote,
		String? EmbeddingEndpointNote);

	/// <summary>
	/// A keyboard for the console under test: each read waits for the test to type the next line, so the
	/// test decides what the host has done before the next line arrives — a scripted string would hand the
	/// loop <c>exit</c> before the test had checked anything.
	/// </summary>
	private sealed class TypedLines : TextReader
	{
		private readonly BlockingCollection<String?> _lines = [];

		public void Type(String line) => this._lines.Add(line);

		public override String? ReadLine() => this._lines.Take();

		protected override void Dispose(Boolean disposing)
		{
			if (disposing)
			{
				this._lines.Dispose();
			}

			base.Dispose(disposing);
		}
	}

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
}
