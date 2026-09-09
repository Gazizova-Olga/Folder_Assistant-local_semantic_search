using System.Threading.Channels;
using FluentAssertions;
using FolderAssistant.Embedding;
using FolderAssistant.Indexing;
using FolderAssistant.Persistence;
using FolderAssistant.Retrieval;
using Microsoft.Extensions.Hosting;

namespace FolderAssistant.Tests;

/// <summary>
/// Indexing moved off the startup path, so the guarantee "the host is up, therefore the index is
/// populated" no longer holds. These cover what replaced it: a state a reader can consult, and a
/// retrieval path that refuses rather than answering from an index that is not finished.
/// </summary>
public sealed class BackgroundIndexingTests
{
	[Fact]
	public void An_Index_That_Has_Not_Run_Is_Building()
	{
		IndexState state = new();

		state.Status.Should().Be(IndexStatus.Building);
		state.LastIndexedUtc.Should().BeNull();
	}

	[Fact]
	public async Task A_Completed_Pass_Makes_The_Index_Queryable()
	{
		IndexState state = new();
		using FolderIndexingService service = new(() => EmptyResult, changeFeed: null, state);

		await service.StartAsync(CancellationToken.None);
		await service.ExecuteTask!;

		state.Status.Should().Be(IndexStatus.Ready);
		state.LastIndexedUtc.Should().NotBeNull();
		state.Error.Should().BeNull();
	}

	/// <summary>
	/// Nothing else observes a background pass. If a failure only reached a log, retrieval would go on
	/// answering as though the folder were merely empty, so the failure has to reach the state.
	/// </summary>
	[Fact]
	public async Task A_Failed_Pass_Leaves_The_Cause_On_The_State()
	{
		InvalidOperationException failure = new("the scan could not read the folder");
		IndexState state = new();
		using FolderIndexingService service = new(() => throw failure, changeFeed: null, state);

		await service.StartAsync(CancellationToken.None);
		await service.ExecuteTask!;

		state.Status.Should().Be(IndexStatus.Failed);
		state.Error.Should().BeSameAs(failure);
	}

	/// <summary>
	/// The failure is contained. A background service whose ExecuteAsync throws would otherwise take
	/// the host down with it, so an unreadable folder would stop the application rather than degrade
	/// one feature of it.
	/// </summary>
	[Fact]
	public async Task A_Failed_Pass_Does_Not_Fault_The_Host()
	{
		IndexState state = new();
		using FolderIndexingService service = new(() => throw new IOException("locked"), changeFeed: null, state);

		await service.StartAsync(CancellationToken.None);

		Func<Task> run = async () => await service.ExecuteTask!;

		await run.Should().NotThrowAsync();
	}

	[Fact]
	public void Retrieval_Refuses_While_The_Index_Is_Building()
	{
		IndexState state = new();

		Func<Object> search = () => Search(state);

		search.Should().Throw<IndexNotReadyException>()
			.WithMessage("*still building*");
	}

	/// <summary>
	/// A failed build is reported as a failure, carrying its cause. It is not the same condition as
	/// "still building" — one resolves by waiting and the other does not — so a caller is given the
	/// original exception rather than a bare refusal.
	/// </summary>
	[Fact]
	public void Retrieval_Refuses_After_A_Failed_Build_And_Carries_The_Cause()
	{
		InvalidOperationException failure = new("the embedding backend is unreachable");
		IndexState state = new();
		state.MarkFailed(failure);

		Func<Object> search = () => Search(state);

		search.Should().Throw<IndexNotReadyException>()
			.WithMessage("*the embedding backend is unreachable*")
			.WithInnerException<InvalidOperationException>();
	}

	[Fact]
	public void Retrieval_Answers_Once_The_Index_Is_Ready()
	{
		IndexState state = new();
		state.MarkReady(DateTime.UtcNow);

		Func<Object> search = () => Search(state);

		search.Should().NotThrow();
	}

	/// <summary>
	/// With indexing switched off nothing will ever build an index, so a query must not block on one.
	/// Program marks the state ready at startup; this pins the property that makes that safe — Ready
	/// over an empty store answers with no hits rather than refusing.
	/// </summary>
	[Fact]
	public void An_Index_Marked_Ready_With_Nothing_In_It_Returns_No_Hits()
	{
		IndexState state = new();
		state.MarkReady(DateTime.UtcNow);

		Search(state).Should().BeEmpty();
	}

	/// <summary>
	/// A signal from the feed runs another pass. Without this the index is correct exactly once, at
	/// startup, and drifts from the folder from the first edit onwards.
	/// </summary>
	[Fact]
	public async Task A_Change_Signal_Runs_Another_Pass()
	{
		using StubChangeFeed feed = new();
		IndexState state = new();
		Int32 passes = 0;

		using FolderIndexingService service = new(
			() =>
			{
				Interlocked.Increment(ref passes);
				return EmptyResult;
			},
			feed,
			state);

		await service.StartAsync(CancellationToken.None);
		await feed.Started;

		feed.Signal("a file changed");

		await WaitUntil(() => Volatile.Read(ref passes) >= 2);

		Volatile.Read(ref passes).Should().BeGreaterThanOrEqualTo(2);
	}

	/// <summary>
	/// A refresh that fails leaves the index serving. The stored vectors are going stale, not
	/// missing, and refusing every query because the folder was briefly unreadable takes a working
	/// feature out of service for a condition the next pass will clear on its own.
	/// </summary>
	[Fact]
	public async Task A_Failed_Refresh_Keeps_The_Existing_Index_Queryable()
	{
		using StubChangeFeed feed = new();
		IndexState state = new();
		Int32 passes = 0;

		using FolderIndexingService service = new(
			() => Interlocked.Increment(ref passes) > 1
				? throw new IOException("the folder went away")
				: EmptyResult,
			feed,
			state);

		await service.StartAsync(CancellationToken.None);
		await WaitUntil(() => state.Status == IndexStatus.Ready);

		await feed.Started;
		feed.Signal("a file changed");

		await WaitUntil(() => Volatile.Read(ref passes) >= 2);

		state.Status.Should().Be(IndexStatus.Ready);
	}

	/// <summary>
	/// Not just the status. An error published against a Ready index makes a healthy index look broken
	/// to anything treating Error as the "is it usable" signal — which is what Error is for.
	/// </summary>
	[Fact]
	public void A_Failed_Refresh_Leaves_A_Ready_Index_Unblemished()
	{
		IndexState state = new();
		state.MarkReady(DateTime.UtcNow);

		state.MarkFailed(new IOException("the folder went away"));

		state.Status.Should().Be(IndexStatus.Ready);
		state.Error.Should().BeNull();
	}

	/// <summary>The same property from the caller's side: the refusal does not come back.</summary>
	[Fact]
	public void Retrieval_Still_Answers_After_A_Refresh_Fails()
	{
		IndexState state = new();
		state.MarkReady(DateTime.UtcNow);
		state.MarkFailed(new IOException("the folder went away"));

		Func<Object> search = () => Search(state);

		search.Should().NotThrow();
	}

	/// <summary>
	/// The distinction only runs one way. A build that has never succeeded stays failed, because
	/// there is nothing stored to keep serving.
	/// </summary>
	[Fact]
	public void A_Failure_Before_Any_Successful_Pass_Leaves_The_Index_Failed()
	{
		IndexState state = new();

		state.MarkFailed(new IOException("the folder went away"));

		state.Status.Should().Be(IndexStatus.Failed);
	}

	private static async Task WaitUntil(Func<Boolean> condition)
	{
		DateTime deadline = DateTime.UtcNow.AddSeconds(5);

		while (!condition() && DateTime.UtcNow < deadline)
		{
			await Task.Delay(10);
		}
	}

	/// <summary>A feed the test drives directly, so no filesystem timing is involved.</summary>
	private sealed class StubChangeFeed : IFileChangeFeed
	{
		private readonly Channel<FolderChangeSignal> _channel = Channel.CreateUnbounded<FolderChangeSignal>();
		private readonly TaskCompletionSource _started =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		/// <summary>Completes when the service has started the feed, which it does after its first pass.</summary>
		public Task Started => this._started.Task;

		public void Start() => this._started.TrySetResult();

		public void Signal(String reason) => this._channel.Writer.TryWrite(new FolderChangeSignal(reason));

		public IAsyncEnumerable<FolderChangeSignal> ReadAllAsync(CancellationToken cancellationToken)
			=> this._channel.Reader.ReadAllAsync(cancellationToken);

		public void Dispose() => this._channel.Writer.TryComplete();
	}

	private static readonly IndexingResult EmptyResult = new(0, 0, 0, 0, 0, 0);

	private static IReadOnlyList<RetrievalHit> Search(IIndexState state)
	{
		CosineRetrievalQuery query = new(
			new ProgrammableEmbeddingVectorizer("test-v1", 16),
			new EmptyVectorStoreReader(),
			state);

		return query.Search("unused.db", "anything", new RetrievalOptions(TopK: 5));
	}

	private sealed class EmptyVectorStoreReader : IVectorStoreReader
	{
		public IReadOnlyList<StoredVector> ReadVectorsByModelVersion(String databasePath, String modelVersionId)
			=> [];

		public IReadOnlyDictionary<String, ChunkLocation> ReadChunkLocations(
			String databasePath,
			IReadOnlyList<String> chunkIds)
			=> new Dictionary<String, ChunkLocation>();

		public String? ReadFitArtifact(String databasePath, String modelVersionId)
			=> null;
	}
}
