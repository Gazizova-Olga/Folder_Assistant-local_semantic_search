using System.Net.Http.Json;
using FluentAssertions;
using FolderAssistant.Indexing;
using FolderAssistant.Indexing.Outbox;
using FolderAssistant.Indexing.Scanning;
using FolderAssistant.Persistence;
using FolderAssistant.Surfaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace FolderAssistant.Tests.Surfaces;

/// <summary>
/// What the index reports while it is being polled (SPEC-120, SPEC-170's neighbour on the surface).
///
/// <para>
/// Through the running host, because the endpoint's subject is what an operator waiting on an index can
/// see: the state the application holds, and the counts the database holds, together.
/// </para>
/// </summary>
public sealed class IndexStatusEndpointTests
{
	private static readonly (String Key, String Value) InProcessProfile = ($"{AgentConfig.SectionName}:Profile", "lsa-blob");

	[Fact]
	public async Task A_Ready_Index_Reports_What_It_Delivered()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("notes.md"), "alpha beta gamma delta epsilon zeta");
		using HostFixture host = new(folder, InProcessProfile);

		using HttpClient client = host.CreateClient();
		await client.GetAsync("/");
		IIndexState state = host.Services.GetRequiredService<IIndexState>();
		await WaitFor(() => state.Status == IndexStatus.Ready);

		IndexStatusDto? status = await client.GetFromJsonAsync<IndexStatusDto>("/api/index/status");

		status.Should().NotBeNull();
		status!.Status.Should().Be("Ready");
		status.Error.Should().BeNull();
		status.LastIndexedUtc.Should().NotBeNull();
		status.Delivered.Should().Be(1);
		status.Pending.Should().Be(0);
		status.Failed.Should().Be(0);
		status.FailedFiles.Should().BeEmpty();
		status.FailedFilesTruncated.Should().BeFalse();
	}

	/// <summary>
	/// A file recorded and not yet delivered is backlog, not a failure, and not something already
	/// indexed. Counting it as delivered is how a folder looks finished while half of it is unsearchable.
	/// </summary>
	[Fact]
	public async Task A_File_Recorded_And_Not_Delivered_Is_Pending()
	{
		using TempFolder folder = new();
		using HostFixture host = new(folder, ($"{AgentConfig.SectionName}:Indexing:Enabled", "false"));

		using HttpClient client = host.CreateClient();
		await client.GetAsync("/");

		String databasePath = folder.Combine(".folderassistant", "manifest.db");
		await new FolderIndexStore(databasePath).ApplyAsync(
			[new ReconciledChange("notes.md", FileDelta.Added, new FileRecord("notes.md", "hash", 10, default))]);

		IndexStatusDto? status = await client.GetFromJsonAsync<IndexStatusDto>("/api/index/status");

		status!.Pending.Should().Be(1);
		status.Delivered.Should().Be(0);
		status.Failed.Should().Be(0);
	}

	/// <summary>
	/// A file given up on is named with the error and the attempts it took, because a search quietly not
	/// finding that file is its only other symptom.
	/// </summary>
	[Fact]
	public async Task A_File_Given_Up_On_Is_Named_With_Its_Error()
	{
		using TempFolder folder = new();
		using HostFixture host = new(folder, ($"{AgentConfig.SectionName}:Indexing:Enabled", "false"));

		using HttpClient client = host.CreateClient();
		await client.GetAsync("/");

		String databasePath = folder.Combine(".folderassistant", "manifest.db");
		FailOne(databasePath, "broken.md", "the embedder said no", attempts: 5);

		IndexStatusDto? status = await client.GetFromJsonAsync<IndexStatusDto>("/api/index/status");

		status!.Failed.Should().Be(1);
		status.FailedFiles.Should().ContainSingle().Which.Should().BeEquivalentTo(
			new { Path = "broken.md", Error = "the embedder said no", Attempts = 5 });
		status.FailedFilesTruncated.Should().BeFalse();
	}

	/// <summary>
	/// The count is exact while the list is a sample, and the flag is what keeps the two from being read
	/// as the same number. A sample taken for the whole is how four hundred unindexed files look like
	/// fifty.
	/// </summary>
	[Fact]
	public async Task More_Failures_Than_The_Sample_Holds_Are_Counted_Exactly_And_Flagged()
	{
		using TempFolder folder = new();
		using HostFixture host = new(folder, ($"{AgentConfig.SectionName}:Indexing:Enabled", "false"));

		using HttpClient client = host.CreateClient();
		await client.GetAsync("/");

		String databasePath = folder.Combine(".folderassistant", "manifest.db");
		Int32 failures = IndexStatusEndpoint.MaxFailedFiles + 7;
		for (Int32 number = 0; number < failures; number++)
		{
			FailOne(databasePath, $"broken-{number:D3}.md", "the embedder said no", attempts: 5);
		}

		IndexStatusDto? status = await client.GetFromJsonAsync<IndexStatusDto>("/api/index/status");

		status!.Failed.Should().Be(failures, "the count is exact");
		status.FailedFiles.Should().HaveCount(IndexStatusEndpoint.MaxFailedFiles, "the list is a sample");
		status.FailedFilesTruncated.Should().BeTrue();
	}

	/// <summary>
	/// One file that failed twice is one failed file. Counting operations would overstate what is missing
	/// from the index, and the number an operator is watching is how many of their files are not there.
	/// </summary>
	[Fact]
	public async Task A_File_That_Failed_Twice_Is_Counted_Once()
	{
		using TempFolder folder = new();
		using HostFixture host = new(folder, ($"{AgentConfig.SectionName}:Indexing:Enabled", "false"));

		using HttpClient client = host.CreateClient();
		await client.GetAsync("/");

		String databasePath = folder.Combine(".folderassistant", "manifest.db");
		FailOne(databasePath, "broken.md", "first failure", attempts: 5);
		FailOne(databasePath, "broken.md", "second failure", attempts: 5);

		IndexStatusDto? status = await client.GetFromJsonAsync<IndexStatusDto>("/api/index/status");

		status!.Failed.Should().Be(1);
		status.FailedFiles.Should().ContainSingle().Which.Error.Should().Be("second failure",
			"the latest attempt is the one worth reading");
	}

	/// <summary>
	/// Polling must not be able to perturb what it reports: the reader opens read-only, so a status
	/// request cannot take the write lock the indexer needs to make the backlog go down.
	/// </summary>
	[Fact]
	public void The_Reader_Cannot_Write()
	{
		using TempFolder folder = new();
		String databasePath = new FolderDatabaseBootstrapper()
			.EnsureInitialized(folder.Path, new PersistenceConfig()).DatabasePath;

		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(databasePath);
		using SqliteCommand command = connection.CreateCommand();
		command.CommandText = "DELETE FROM file_manifest;";

		FluentActions.Invoking(command.ExecuteNonQuery).Should().Throw<SqliteException>();
	}

	/// <summary>
	/// The surface is served on loopback and nowhere else (SPEC-920): there is no authentication, so binding
	/// anywhere else would make these endpoints an unauthenticated service.
	///
	/// <para>
	/// This asserts the rule the composition root applies, not the socket: a test host replaces the server,
	/// so nothing here proves what Kestrel bound. What it does catch is the change that would do the damage —
	/// a wildcard or a non-loopback address put where the rule is decided.
	/// </para>
	/// </summary>
	[Theory]
	[InlineData(5080)]
	[InlineData(80)]
	public void The_Server_Listens_On_Loopback_Only(Int32 port)
	{
		String url = Program.ListenUrl(port);

		new Uri(url).IsLoopback.Should().BeTrue("binding anywhere else makes an unauthenticated service");
		url.Should().NotContain("*").And.NotContain("+").And.NotContain("0.0.0.0");
		new Uri(url).Port.Should().Be(port);
	}

	/// <summary>Queues one delivery and retires it failed, as the dispatcher does when it gives up.</summary>
	private static void FailOne(String databasePath, String relativePath, String error, Int32 attempts)
	{
		using SqliteConnection connection = FolderDatabaseConnection.OpenWrite(databasePath);
		using SqliteCommand command = connection.CreateCommand();

		command.CommandText = $"""
			INSERT INTO outbox (file_path, op_type, status, attempts, created_utc, next_attempt_utc, error)
			VALUES ($path, {(Int32)DeliveryKind.Upsert}, {FolderIndexStore.Failed}, $attempts, $now, $now, $error);
			""";
		command.Parameters.AddWithValue("$path", relativePath);
		command.Parameters.AddWithValue("$attempts", attempts);
		command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
		command.Parameters.AddWithValue("$error", error);
		command.ExecuteNonQuery();
	}

	private static async Task WaitFor(Func<Boolean> condition)
	{
		DateTime deadline = DateTime.UtcNow.AddSeconds(15);
		while (!condition() && DateTime.UtcNow < deadline)
		{
			await Task.Delay(25);
		}

		condition().Should().BeTrue("the index should have become ready");
	}

	private sealed record IndexStatusDto(
		String Status,
		String? Error,
		DateTime? LastIndexedUtc,
		Int64 Delivered,
		Int64 Pending,
		Int64 Failed,
		IReadOnlyList<FailedFileDto> FailedFiles,
		Boolean FailedFilesTruncated);

	private sealed record FailedFileDto(String Path, String? Error, Int32 Attempts);
}
