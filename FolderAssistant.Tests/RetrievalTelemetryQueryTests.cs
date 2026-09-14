using FluentAssertions;
using FolderAssistant.Indexing;
using FolderAssistant.Retrieval;

namespace FolderAssistant.Tests;

/// <summary>
/// The retrieval telemetry decorator: what it records for each of the four outcomes, and that
/// observing an outcome never changes what the caller gets.
/// </summary>
public sealed class RetrievalTelemetryQueryTests
{
	private static readonly RetrievalOptions Options = new(TopK: 7);

	[Fact]
	public void A_Successful_Search_Records_One_Event_And_Returns_Its_Hits_Untouched()
	{
		RecordingTelemetry telemetry = new();
		IReadOnlyList<RetrievalHit> hits = [new RetrievalHit("chunk-1", "notes.md", 0, 0, 12, 0.82)];
		IRetrievalQuery query = RetrievalTelemetryQuery.Wrap(new StubRetrievalQuery { Hits = hits }, telemetry);

		IReadOnlyList<RetrievalHit> returned = query.Search("manifest.db", "how does indexing work", Options);

		returned.Should().BeSameAs(hits);

		telemetry.Calls.Should().ContainSingle();
		RetrievalCallTelemetry call = telemetry.Calls[0];
		call.Status.Should().Be(RetrievalStatus.Success);
		call.RequestedTopK.Should().Be(7);
		call.ResultCount.Should().Be(1);
		call.ErrorCode.Should().BeNull();
	}

	/// <summary>
	/// The number recorded is what the caller asked for, not what came back. They differ whenever the
	/// index holds fewer matches than the request, and only the ask explains the latency.
	/// </summary>
	[Fact]
	public void The_Recorded_TopK_Is_The_Request_Not_The_Result_Count()
	{
		RecordingTelemetry telemetry = new();
		IRetrievalQuery query = RetrievalTelemetryQuery.Wrap(new StubRetrievalQuery(), telemetry);

		query.Search("manifest.db", "a query nothing matches", new RetrievalOptions(TopK: 20));

		telemetry.Calls[0].RequestedTopK.Should().Be(20);
		telemetry.Calls[0].ResultCount.Should().Be(0);
	}

	[Fact]
	public void An_Index_That_Is_Still_Building_Is_Recorded_As_NotReady_And_Still_Refuses()
	{
		RecordingTelemetry telemetry = new();
		IRetrievalQuery query = RetrievalTelemetryQuery.Wrap(
			new StubRetrievalQuery { ThrowWith = () => new IndexNotReadyException("still building") }, telemetry);

		Action search = () => query.Search("manifest.db", "anything", Options);

		// Refusing is the caller's signal to wait; watching it happen is not permission to answer.
		search.Should().Throw<IndexNotReadyException>();

		telemetry.Calls.Should().ContainSingle();
		telemetry.Calls[0].Status.Should().Be(RetrievalStatus.NotReady);
		telemetry.Calls[0].ErrorCode.Should().BeNull();
	}

	/// <summary>
	/// The same exception type carries the fault case, and it must not be filed under the benign one.
	/// A build that failed is usually a backend that stopped answering, and reporting it as "not ready
	/// yet" describes it as a condition that will clear on its own.
	/// </summary>
	[Fact]
	public void An_Index_That_Failed_To_Build_Is_Recorded_As_Failed_Not_NotReady()
	{
		RecordingTelemetry telemetry = new();
		IRetrievalQuery query = RetrievalTelemetryQuery.Wrap(
			new StubRetrievalQuery
			{
				ThrowWith = () => new IndexNotReadyException(
					"the index failed to build",
					new HttpRequestException("connection refused"),
					isBuildFailure: true),
			},
			telemetry);

		Action search = () => query.Search("manifest.db", "anything", Options);

		search.Should().Throw<IndexNotReadyException>();

		telemetry.Calls[0].Status.Should().Be(RetrievalStatus.Failed);

		// The type that actually broke, not the refusal wrapped around it. The wrapper is the same for
		// both conditions and names neither cause.
		telemetry.Calls[0].ErrorCode.Should().Be(nameof(HttpRequestException));
	}

	/// <summary>
	/// The distinction above is only worth anything if the guard sets it. Constructing the exception by
	/// hand proves the decorator reads a flag; this proves something writes it.
	/// </summary>
	[Fact]
	public void The_Guard_Marks_A_Failed_Build_And_Leaves_A_Building_One_Unmarked()
	{
		StubIndexState building = new() { Status = IndexStatus.Building };
		StubIndexState failed = new() { Status = IndexStatus.Failed, Error = new InvalidOperationException("no backend") };

		Action queryWhileBuilding = () => RetrievalGuard.EnsureQueryable(building);
		Action queryAfterFailure = () => RetrievalGuard.EnsureQueryable(failed);

		queryWhileBuilding.Should().Throw<IndexNotReadyException>()
			.Which.IsBuildFailure.Should().BeFalse();

		queryAfterFailure.Should().Throw<IndexNotReadyException>()
			.Which.IsBuildFailure.Should().BeTrue();
	}

	/// <summary>
	/// A search embeds its query text before it ranks anything, so an embedding backend that stops
	/// answering ends the search — as a cancellation, since the HTTP client's own deadline is what
	/// gives up. Charging that to the vector backend would blame whichever one happened to be composed.
	/// </summary>
	[Fact]
	public void A_Cancelled_Search_Is_Recorded_As_TimedOut_Not_Failed()
	{
		RecordingTelemetry telemetry = new();
		IRetrievalQuery query = RetrievalTelemetryQuery.Wrap(
			new StubRetrievalQuery { ThrowWith = () => new TaskCanceledException("the embed never returned") },
			telemetry);

		Action search = () => query.Search("manifest.db", "anything", Options);

		search.Should().Throw<TaskCanceledException>();

		telemetry.Calls[0].Status.Should().Be(RetrievalStatus.TimedOut);
		telemetry.Calls[0].ErrorCode.Should().Be(nameof(TaskCanceledException));
	}

	/// <summary>
	/// The embedder's own deadline arrives as a timeout rather than a cancellation (SPEC-162), and it is
	/// the same fault in the same place: outside the backend being measured.
	/// </summary>
	[Fact]
	public void A_Search_Whose_Embed_Hit_Its_Deadline_Is_Recorded_As_TimedOut_Not_Failed()
	{
		RecordingTelemetry telemetry = new();
		IRetrievalQuery query = RetrievalTelemetryQuery.Wrap(
			new StubRetrievalQuery { ThrowWith = () => new TimeoutException("the embed hit its deadline") },
			telemetry);

		Action search = () => query.Search("manifest.db", "anything", Options);

		search.Should().Throw<TimeoutException>();

		telemetry.Calls[0].Status.Should().Be(RetrievalStatus.TimedOut);
		telemetry.Calls[0].ErrorCode.Should().Be(nameof(TimeoutException));
	}

	[Fact]
	public void A_Genuine_Fault_Is_Recorded_As_Failed_And_Still_Reaches_The_Caller()
	{
		RecordingTelemetry telemetry = new();
		IRetrievalQuery query = RetrievalTelemetryQuery.Wrap(
			new StubRetrievalQuery { ThrowWith = () => new InvalidOperationException("the store is unreadable") },
			telemetry);

		Action search = () => query.Search("manifest.db", "anything", Options);

		search.Should().Throw<InvalidOperationException>().WithMessage("the store is unreadable");

		telemetry.Calls.Should().ContainSingle();
		telemetry.Calls[0].Status.Should().Be(RetrievalStatus.Failed);
		telemetry.Calls[0].ErrorCode.Should().Be(nameof(InvalidOperationException));
		telemetry.Calls[0].ResultCount.Should().Be(0);
	}

	/// <summary>
	/// Which backend produced a figure is the reason for recording it: the two implementations exist to
	/// be compared, and a merged series would describe neither.
	/// </summary>
	[Fact]
	public void The_Recorded_Backend_Names_The_Wrapped_Implementation()
	{
		RecordingTelemetry telemetry = new();

		RetrievalTelemetryQuery.Wrap(new StubRetrievalQuery(), telemetry).Search("db", "q", Options);
		RetrievalTelemetryQuery.Wrap(new SecondStubRetrievalQuery(), telemetry).Search("db", "q", Options);

		telemetry.Calls.Select(call => call.Backend).Should()
			.Equal(nameof(StubRetrievalQuery), nameof(SecondStubRetrievalQuery));
	}

	private sealed class RecordingTelemetry : IRetrievalTelemetry
	{
		public List<RetrievalCallTelemetry> Calls { get; } = [];

		public void Record(RetrievalCallTelemetry call) => this.Calls.Add(call);
	}

	private class StubRetrievalQuery : IRetrievalQuery
	{
		public IReadOnlyList<RetrievalHit> Hits { get; init; } = [];

		public Func<Exception>? ThrowWith { get; init; }

		public IReadOnlyList<RetrievalHit> Search(String databasePath, String queryText, RetrievalOptions options)
			=> this.ThrowWith is null ? this.Hits : throw this.ThrowWith();
	}

	private sealed class SecondStubRetrievalQuery : StubRetrievalQuery;

	private sealed class StubIndexState : IIndexState
	{
		public IndexStatus Status { get; init; }

		public DateTime? LastIndexedUtc => null;

		public Exception? Error { get; init; }
	}
}
