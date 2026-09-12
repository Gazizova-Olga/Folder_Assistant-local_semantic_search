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

	// ── A failed first index is not permanent ─────────────────────────────────

	/// <summary>
	/// What fails a first index is usually outside this process and usually temporary — an embedding
	/// backend that has not finished starting, a model still being pulled. Left at
	/// <see cref="IndexStatus.Failed"/>, every search refuses for the life of the process, and the
	/// remedy is restarting an application that would have recovered on its own.
	/// </summary>
	[Fact]
	public async Task A_Failed_First_Pass_Is_Tried_Again_Until_It_Succeeds()
	{
		Int32 attempts = 0;
		IndexState state = new();

		using FolderIndexingService service = new(
			() => ++attempts < 3 ? throw new IOException("the embedding backend is not up yet") : EmptyResult,
			changeFeed: null,
			state,
			healthCheck: null,
			failedRetryInterval: TimeSpan.FromMilliseconds(20));

		await service.StartAsync(CancellationToken.None);
		await FinishedWithin(service);

		attempts.Should().Be(3);
		state.Status.Should().Be(IndexStatus.Ready);
	}

	/// <summary>
	/// The probe is the likeliest thing to fail first and the likeliest to fix itself, since it is
	/// usually a backend that is merely slower to start than this process.
	/// </summary>
	[Fact]
	public async Task A_Failing_Probe_Is_Tried_Again_Until_It_Passes()
	{
		Int32 probes = 0;
		Int32 passes = 0;
		IndexState state = new();

		StubHealthCheck probe = new(() =>
		{
			if (++probes < 3)
			{
				throw new InvalidOperationException("the embedding backend is unreachable");
			}
		});

		using FolderIndexingService service = new(
			() => { passes++; return EmptyResult; },
			changeFeed: null,
			state,
			probe,
			TimeSpan.FromMilliseconds(20));

		await service.StartAsync(CancellationToken.None);
		await FinishedWithin(service);

		probes.Should().Be(3);
		passes.Should().Be(1, "a probe that never passed must not let an index run");
		state.Status.Should().Be(IndexStatus.Ready);
	}

	/// <summary>
	/// A pass can fail on either side of the probe. Re-asking a backend that has already answered
	/// costs a round trip for an answer that has not changed, where the part worth repeating is the
	/// pass that failed after it.
	/// </summary>
	[Fact]
	public async Task A_Probe_That_Has_Passed_Is_Not_Asked_Again()
	{
		Int32 probes = 0;
		Int32 attempts = 0;
		IndexState state = new();

		StubHealthCheck probe = new(() => probes++);

		using FolderIndexingService service = new(
			() => ++attempts < 3 ? throw new IOException("the folder is locked") : EmptyResult,
			changeFeed: null,
			state,
			probe,
			TimeSpan.FromMilliseconds(20));

		await service.StartAsync(CancellationToken.None);
		await FinishedWithin(service);

		attempts.Should().Be(3);
		probes.Should().Be(1, "the backend already answered; the pass after it is what failed");
	}

	/// <summary>
	/// Zero is the opt-out, and it has to keep working: a deployment that would rather see a failure
	/// stand than have the process keep trying can still say so.
	/// </summary>
	[Fact]
	public async Task Without_An_Interval_The_First_Attempt_Is_The_Only_One()
	{
		Int32 attempts = 0;
		IndexState state = new();

		using FolderIndexingService service = new(
			() => { attempts++; throw new IOException("the folder is locked"); },
			changeFeed: null,
			state,
			healthCheck: null,
			failedRetryInterval: TimeSpan.Zero);

		await service.StartAsync(CancellationToken.None);
		await FinishedWithin(service);

		attempts.Should().Be(1);
		state.Status.Should().Be(IndexStatus.Failed);
	}

	/// <summary>
	/// Stopping must not take as long as the retry schedule. The interval is a statement about how
	/// long to keep trying to recover, not about how long a host may take to shut down — and a
	/// generous one is exactly what a deployment would choose.
	/// </summary>
	[Fact]
	public async Task Stopping_Inside_A_Retry_Wait_Does_Not_Wait_It_Out()
	{
		IndexState state = new();

		using FolderIndexingService service = new(
			() => throw new IOException("the folder is locked"),
			changeFeed: null,
			state,
			healthCheck: null,
			failedRetryInterval: TimeSpan.FromMinutes(5));

		await service.StartAsync(CancellationToken.None);

		// The first attempt has to have failed, or this would stop a service that never began waiting.
		DateTime deadline = DateTime.UtcNow.AddSeconds(20);

		while (state.Status != IndexStatus.Failed && DateTime.UtcNow < deadline)
		{
			await Task.Delay(10);
		}

		Func<Task> stop = async () => await service.StopAsync(CancellationToken.None);

		await stop.Should().CompleteWithinAsync(TimeSpan.FromSeconds(20));
	}


	/// <summary>
	/// Waits for the pass loop with a bound rather than forever. A retry loop that stopped
	/// terminating would otherwise hang the run instead of failing it, and a suite that hangs says
	/// less than one that goes red — found by mutating the opt-out and watching the run stop
	/// answering instead of reporting.
	/// </summary>
	private static async Task FinishedWithin(FolderIndexingService service)
	{
		Func<Task> run = async () => await service.ExecuteTask!;

		await run.Should().CompleteWithinAsync(TimeSpan.FromSeconds(20));
	}

	/// <summary>A probe whose answer the test decides, call by call.</summary>
	private sealed class StubHealthCheck(Action onCheck) : IEmbeddingHealthCheck
	{
		public ValueTask CheckAsync(CancellationToken cancellationToken = default)
		{
			onCheck();

			return ValueTask.CompletedTask;
		}
	}

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

		public IReadOnlySet<String> ReadFileIdsWithVectors(String databasePath, String modelVersionId)
			=> new HashSet<String>(StringComparer.OrdinalIgnoreCase);

		public IReadOnlyDictionary<String, ChunkLocation> ReadChunkLocations(
			String databasePath,
			IReadOnlyList<String> chunkIds)
			=> new Dictionary<String, ChunkLocation>();

		public String? ReadFitArtifact(String databasePath, String modelVersionId)
			=> null;
	}
}
