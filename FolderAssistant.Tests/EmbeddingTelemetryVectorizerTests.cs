using FluentAssertions;
using FolderAssistant.Embedding;

namespace FolderAssistant.Tests;

/// <summary>
/// The telemetry decorator: what it records, what it stamps, and — most importantly — what it must not
/// hide about the vectorizer underneath it.
/// </summary>
public sealed class EmbeddingTelemetryVectorizerTests
{
	[Fact]
	public async Task A_Successful_Call_Records_One_Event_Describing_The_Model_And_The_Batch()
	{
		RecordingTelemetry telemetry = new();
		IVectorizer vectorizer = EmbeddingTelemetryVectorizer.Wrap(new StubVectorizer(), telemetry);

		await vectorizer.VectorizeAsync(["one", "two"], EmbeddingKind.Document);

		telemetry.Calls.Should().ContainSingle();

		EmbeddingCallTelemetry call = telemetry.Calls[0];
		call.Status.Should().Be(EmbeddingStatus.Success);
		call.ProviderType.Should().Be("stub");
		call.RequestCount.Should().Be(2);
		call.ErrorCode.Should().BeNull();
	}

	/// <summary>
	/// The latency belongs to the call, so every result of one batch carries the same figure. A per-text
	/// value would be a fiction: one round-trip embedded all of them.
	/// </summary>
	[Fact]
	public async Task Every_Result_In_A_Batch_Carries_The_Same_Measured_Latency()
	{
		IVectorizer vectorizer = EmbeddingTelemetryVectorizer.Wrap(new StubVectorizer(), new RecordingTelemetry());

		IReadOnlyList<EmbeddingResult> results =
			await vectorizer.VectorizeAsync(["one", "two", "three"], EmbeddingKind.Document);

		results.Should().HaveCount(3);
		results.Select(result => result.LatencyMs).Distinct().Should().ContainSingle();
	}

	/// <summary>
	/// A failing backend is the case an operator most needs to see, so the event is recorded on the way
	/// out and the exception still reaches the caller.
	/// </summary>
	[Fact]
	public async Task A_Failed_Call_Is_Recorded_And_Still_Throws()
	{
		RecordingTelemetry telemetry = new();
		IVectorizer vectorizer = EmbeddingTelemetryVectorizer.Wrap(
			new StubVectorizer { ThrowWith = () => new InvalidOperationException("backend said no") }, telemetry);

		Func<Task> embed = async () => await vectorizer.VectorizeAsync(["one"], EmbeddingKind.Document);

		await embed.Should().ThrowAsync<InvalidOperationException>();

		telemetry.Calls.Should().ContainSingle();
		telemetry.Calls[0].Status.Should().Be(EmbeddingStatus.Failed);
		telemetry.Calls[0].ErrorCode.Should().Be(nameof(InvalidOperationException));
	}

	/// <summary>
	/// Cancellation is separated from failure. An error rate that counts shutdowns tells an operator to
	/// investigate a process exiting normally.
	/// </summary>
	[Fact]
	public async Task A_Cancelled_Call_Is_Recorded_As_TimedOut_Not_Failed()
	{
		RecordingTelemetry telemetry = new();
		IVectorizer vectorizer = EmbeddingTelemetryVectorizer.Wrap(
			new StubVectorizer { ThrowWith = () => new OperationCanceledException() }, telemetry);

		Func<Task> embed = async () => await vectorizer.VectorizeAsync(["one"], EmbeddingKind.Document);

		await embed.Should().ThrowAsync<OperationCanceledException>();

		telemetry.Calls[0].Status.Should().Be(EmbeddingStatus.TimedOut);
	}

	/// <summary>
	/// The failure mode this decorator could most easily introduce, and the reason <c>Wrap</c> exists.
	///
	/// <para>
	/// The cold-fit path finds a fittable vectorizer by type test. A wrapper that did not forward the
	/// capability would answer "no" while still returning perfectly good vectors — so fitting would
	/// silently stop happening, and the only symptom would be worse retrieval.
	/// </para>
	/// </summary>
	[Fact]
	public void Wrapping_A_Fittable_Vectorizer_Keeps_It_Fittable()
	{
		IVectorizer wrapped = EmbeddingTelemetryVectorizer.Wrap(new StubFittable(), new RecordingTelemetry());

		wrapped.Should().BeAssignableTo<IFittableVectorizer>();
		((IFittableVectorizer)wrapped).Fit(["corpus"]).Should().Be("fitted");
	}

	/// <summary>The same property for the other capability: the startup probe must survive wrapping.</summary>
	[Fact]
	public async Task Wrapping_A_Health_Checked_Vectorizer_Keeps_It_Health_Checkable()
	{
		StubHealthChecked inner = new();
		IVectorizer wrapped = EmbeddingTelemetryVectorizer.Wrap(inner, new RecordingTelemetry());

		wrapped.Should().BeAssignableTo<IEmbeddingHealthCheck>();

		await ((IEmbeddingHealthCheck)wrapped).CheckAsync();

		inner.Checked.Should().BeTrue("the probe must reach the vectorizer that can actually answer it");
	}

	/// <summary>
	/// The container holds the wrapper, so disposing it has to release the inner one — the Ollama
	/// vectorizer owns a client, and a wrapper that swallowed Dispose would leak it at shutdown.
	/// </summary>
	[Fact]
	public void Disposing_The_Wrapper_Disposes_What_It_Wrapped()
	{
		DisposableStub inner = new();
		IVectorizer wrapped = EmbeddingTelemetryVectorizer.Wrap(inner, new RecordingTelemetry());

		((IDisposable)wrapped).Dispose();

		inner.Disposed.Should().BeTrue();
	}

	private sealed class RecordingTelemetry : IEmbeddingTelemetry
	{
		public List<EmbeddingCallTelemetry> Calls { get; } = [];

		public void Record(EmbeddingCallTelemetry call) => this.Calls.Add(call);
	}

	private class StubVectorizer : IVectorizer
	{
		public ModelDescriptor Descriptor { get; } = new("stub-v1", "stub", "stub-model", 3, "cosine");

		public Func<Exception>? ThrowWith { get; init; }

		public ValueTask<IReadOnlyList<EmbeddingResult>> VectorizeAsync(
			IReadOnlyList<String> texts,
			EmbeddingKind kind,
			CancellationToken cancellationToken = default)
		{
			if (this.ThrowWith is not null)
			{
				throw this.ThrowWith();
			}

			IReadOnlyList<EmbeddingResult> results = [.. texts.Select(_ =>
				new EmbeddingResult(new Single[3], this.Descriptor.ModelVersionId, 3, "stub"))];

			return new ValueTask<IReadOnlyList<EmbeddingResult>>(results);
		}
	}

	/// <summary>Sealed, so it may implement IDisposable without the full dispose pattern.</summary>
	private sealed class DisposableStub : StubVectorizer, IDisposable
	{
		public Boolean Disposed { get; private set; }

		public void Dispose() => this.Disposed = true;
	}

	private sealed class StubFittable : StubVectorizer, IFittableVectorizer
	{
		public String Fit(IReadOnlyList<String> corpus) => "fitted";

		public void LoadFit(String artifactJson)
		{
		}
	}

	private sealed class StubHealthChecked : StubVectorizer, IEmbeddingHealthCheck
	{
		public Boolean Checked { get; private set; }

		public ValueTask CheckAsync(CancellationToken cancellationToken = default)
		{
			this.Checked = true;

			return ValueTask.CompletedTask;
		}
	}
}
