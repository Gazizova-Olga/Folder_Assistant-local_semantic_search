using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using FolderAssistant.Embedding;
using Microsoft.Extensions.AI;
using OpenAI;

namespace FolderAssistant.Tests;

/// <summary>
/// The Ollama embedder against a fake generator, so none of this needs a live server.
///
/// <para>
/// What is worth testing here is not the transport — that is somebody else's client — but the two
/// decisions this class makes on top of it: which side of a query/document pair gets the model's
/// instruction prefix, and what happens when the model returns a width the composed model version does
/// not expect. The last section is the exception: the two SDK defaults the client is built without,
/// one held on the options and one observed through the real client against a loopback listener.
/// </para>
/// </summary>
public sealed class OllamaEmbeddingVectorizerTests
{
	private const String Model = "qwen3-embedding:0.6b";
	private const String ModelVersionId = "qwen3-test-v1";
	private const Int32 Dimension = 4;

	/// <summary>Long enough that no test here reaches it by accident; the deadline tests choose their own.</summary>
	private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

	/// <summary>
	/// Qwen3-Embedding is asymmetric: the query side carries a retrieval instruction. Getting this wrong
	/// does not fail, it quietly degrades ranking, so it is pinned by looking at what the model was asked.
	/// </summary>
	[Fact]
	public async Task A_Query_Is_Sent_With_The_Model_Instruction_Prefix()
	{
		FakeEmbeddingGenerator generator = new(Dimension);
		using OllamaEmbeddingVectorizer vectorizer = new(generator, Model, ModelVersionId, Dimension, Deadline);

		await vectorizer.VectorizeAsync(["chocolate cake"], EmbeddingKind.Query);

		generator.LastInputs.Should().ContainSingle();
		generator.LastInputs[0].Should().StartWith("Instruct:").And.EndWith("chocolate cake");
	}

	/// <summary>The other half of the same property: a document must reach the model untouched.</summary>
	[Fact]
	public async Task A_Document_Is_Sent_Verbatim()
	{
		FakeEmbeddingGenerator generator = new(Dimension);
		using OllamaEmbeddingVectorizer vectorizer = new(generator, Model, ModelVersionId, Dimension, Deadline);

		await vectorizer.VectorizeAsync(["chocolate cake"], EmbeddingKind.Document);

		generator.LastInputs.Should().Equal("chocolate cake");
	}

	[Fact]
	public async Task Results_Carry_The_Composed_Model_Version_And_Provider()
	{
		FakeEmbeddingGenerator generator = new(Dimension);
		using OllamaEmbeddingVectorizer vectorizer = new(generator, Model, ModelVersionId, Dimension, Deadline);

		IReadOnlyList<EmbeddingResult> results =
			await vectorizer.VectorizeAsync(["one", "two"], EmbeddingKind.Document);

		results.Should().HaveCount(2);
		results.Should().OnlyContain(result => result.ModelVersionId == ModelVersionId);
		results.Should().OnlyContain(result => result.ProviderType == "ollama");
		results.Should().OnlyContain(result => result.Vector.Count == Dimension);
	}

	/// <summary>
	/// The failure that matters most. Vectors are keyed <c>(chunk_id, model_version_id)</c> and that space
	/// assumes one width, so a model emitting a different one has to stop the write rather than pollute it
	/// — every later query under that version would be compared against something it does not match.
	/// </summary>
	[Fact]
	public async Task A_Width_The_Model_Version_Does_Not_Expect_Fails_Rather_Than_Being_Stored()
	{
		FakeEmbeddingGenerator generator = new(width: Dimension + 1);
		using OllamaEmbeddingVectorizer vectorizer = new(generator, Model, ModelVersionId, Dimension, Deadline);

		Func<Task> embed = async () => await vectorizer.VectorizeAsync(["text"], EmbeddingKind.Document);

		(await embed.Should().ThrowAsync<InvalidOperationException>())
			.Which.Message.Should().Contain($"{Dimension + 1}").And.Contain($"{Dimension}");
	}

	/// <summary>
	/// A count mismatch is pairing corruption rather than a bad vector: results are matched to inputs by
	/// position, so a short reply would mislabel every one of them.
	/// </summary>
	[Fact]
	public async Task Fewer_Embeddings_Than_Inputs_Fails_Rather_Than_Pairing_By_Position()
	{
		FakeEmbeddingGenerator generator = new(Dimension) { EmitAtMost = 1 };
		using OllamaEmbeddingVectorizer vectorizer = new(generator, Model, ModelVersionId, Dimension, Deadline);

		Func<Task> embed = async () =>
			await vectorizer.VectorizeAsync(["one", "two"], EmbeddingKind.Document);

		await embed.Should().ThrowAsync<InvalidOperationException>();
	}

	/// <summary>An empty batch must not become a round-trip asking a model server to embed nothing.</summary>
	[Fact]
	public async Task An_Empty_Batch_Calls_Nothing()
	{
		FakeEmbeddingGenerator generator = new(Dimension);
		using OllamaEmbeddingVectorizer vectorizer = new(generator, Model, ModelVersionId, Dimension, Deadline);

		IReadOnlyList<EmbeddingResult> results =
			await vectorizer.VectorizeAsync([], EmbeddingKind.Document);

		results.Should().BeEmpty();
		generator.Calls.Should().Be(0);
	}

	/// <summary>
	/// The probe's happy path: a backend that answers is reported usable, in one attempt.
	/// </summary>
	[Fact]
	public async Task A_Reachable_Backend_Passes_The_Health_Check_First_Time()
	{
		FakeEmbeddingGenerator generator = new(Dimension);
		using OllamaEmbeddingVectorizer vectorizer = new(generator, Model, ModelVersionId, Dimension, Deadline);

		await vectorizer.CheckAsync();

		generator.Calls.Should().Be(1, "a backend that answers should not be probed again");
	}

	/// <summary>
	/// A server still paging the model in can refuse the first probe, so the check retries. What it must
	/// not do is mistake that for an absent server on the first refusal.
	/// </summary>
	[Fact]
	public async Task An_Unreachable_Backend_Is_Retried_Before_Being_Declared_Unusable()
	{
		FakeEmbeddingGenerator generator = new(Dimension) { ThrowWith = () => new HttpRequestException("refused") };
		using OllamaEmbeddingVectorizer vectorizer = new(generator, Model, ModelVersionId, Dimension, Deadline);

		Func<Task> check = async () => await vectorizer.CheckAsync();

		(await check.Should().ThrowAsync<InvalidOperationException>())
			.Which.Message.Should().Contain(Model);

		generator.Calls.Should().Be(3, "a slow start deserves more than one chance");
	}

	/// <summary>
	/// The counter-case, and the reason the retry is not blanket. A width mismatch is a configuration
	/// error: it will fail identically three times, so retrying only delays the same answer.
	/// </summary>
	[Fact]
	public async Task A_Width_Mismatch_Fails_The_Health_Check_Without_Retrying()
	{
		FakeEmbeddingGenerator generator = new(width: Dimension + 1);
		using OllamaEmbeddingVectorizer vectorizer = new(generator, Model, ModelVersionId, Dimension, Deadline);

		Func<Task> check = async () => await vectorizer.CheckAsync();

		await check.Should().ThrowAsync<InvalidOperationException>();

		generator.Calls.Should().Be(1, "a configuration error is not transient");
	}

	// ── the deadline ──────────────────────────────────────────────────────────

	/// <summary>
	/// A call that never returns is the failure a delivery could not recover from: held in flight with
	/// its attempts at zero, nothing retries it and nothing gives up. The deadline ends it as a timeout —
	/// not a cancellation, because nobody cancelled, and a decorator filing it as one would describe a
	/// shutdown.
	/// </summary>
	[Fact]
	public async Task A_Call_That_Never_Returns_Fails_As_A_Timeout_Within_Its_Deadline()
	{
		FakeEmbeddingGenerator generator = new(Dimension) { HangUntilCancelled = true };
		using OllamaEmbeddingVectorizer vectorizer = new(
			generator, Model, ModelVersionId, Dimension, TimeSpan.FromMilliseconds(100));

		Stopwatch elapsed = Stopwatch.StartNew();
		Func<Task> embed = async () => await vectorizer.VectorizeAsync(["text"], EmbeddingKind.Document);

		(await embed.Should().ThrowAsync<TimeoutException>()).Which.Message.Should().Contain(Model);

		elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "the deadline is what ended the call");
	}

	/// <summary>
	/// The other side of the same rule: the caller's own cancellation stays a cancellation. The two are
	/// told apart by the caller's token, not by the exception type, because the transport reports its
	/// deadline as a cancellation too.
	/// </summary>
	[Fact]
	public async Task The_Callers_Own_Cancellation_Is_Still_Reported_As_A_Cancellation()
	{
		FakeEmbeddingGenerator generator = new(Dimension) { HangUntilCancelled = true };
		using OllamaEmbeddingVectorizer vectorizer = new(generator, Model, ModelVersionId, Dimension, Deadline);

		using CancellationTokenSource caller = new(TimeSpan.FromMilliseconds(50));
		Func<Task> embed = async () => await vectorizer.VectorizeAsync(["text"], EmbeddingKind.Document, caller.Token);

		await embed.Should().ThrowAsync<OperationCanceledException>();
	}

	/// <summary>
	/// A probe that hits the deadline is a transient like a refusal — a model still paging in on its
	/// first call — so it is retried, and bounded each time, so the probe itself cannot hang.
	/// </summary>
	[Fact]
	public async Task A_Probe_That_Hits_The_Deadline_Is_Retried_Like_A_Refusal()
	{
		FakeEmbeddingGenerator generator = new(Dimension) { HangUntilCancelled = true };
		using OllamaEmbeddingVectorizer vectorizer = new(
			generator, Model, ModelVersionId, Dimension, TimeSpan.FromMilliseconds(50));

		Func<Task> check = async () => await vectorizer.CheckAsync();

		await check.Should().ThrowAsync<InvalidOperationException>();

		generator.Calls.Should().Be(3);
	}

	[Fact]
	public void A_Deadline_Of_Zero_Is_Refused_At_Construction()
	{
		FakeEmbeddingGenerator generator = new(Dimension);

		FluentActions.Invoking(() => new OllamaEmbeddingVectorizer(generator, Model, ModelVersionId, Dimension, TimeSpan.Zero))
			.Should().Throw<ArgumentOutOfRangeException>("a call that may run forever is the defect the deadline exists to close");
	}

	// ── the client underneath ─────────────────────────────────────────────────

	/// <summary>
	/// The client's network timeout is the configured deadline, not the SDK's 100 s: with nothing retrying
	/// underneath the call, the SDK's own would otherwise be the effective deadline for any configured
	/// value above it. Held on the options, because a deadline above 100 s is not a thing to observe in a
	/// test. The retry policy says nothing about how many retries it holds, so it is asserted by what it
	/// does, in the next test.
	/// </summary>
	[Fact]
	public void The_Clients_Network_Timeout_Is_The_Configured_Deadline()
	{
		OpenAIClientOptions options = OllamaEmbeddingVectorizer.ClientOptions("http://127.0.0.1:11434/v1", Deadline);

		options.NetworkTimeout.Should().Be(Deadline);
		options.Endpoint.Should().Be(new Uri("http://127.0.0.1:11434/v1"));
	}

	/// <summary>
	/// The SDK retries a failed connection three times underneath the call, and the probe and the
	/// dispatcher each retry above it; the three layers stacked cost a server that was not there four
	/// connections per attempt, reported as "Retry failed after 4 tries" rather than as the refusal.
	/// Observed through the real client against a loopback listener that accepts and drops every
	/// connection: the call fails after exactly one.
	/// </summary>
	[Fact]
	public async Task A_Failed_Connection_Is_Not_Retried_Underneath_The_Call()
	{
		using TcpListener listener = new(IPAddress.Loopback, 0);
		listener.Start();
		Int32 port = ((IPEndPoint)listener.LocalEndpoint).Port;
		Int32 accepted = 0;
		Task accepting = Task.Run(async () =>
		{
			try
			{
				while (true)
				{
					using TcpClient dropped = await listener.AcceptTcpClientAsync();
					Interlocked.Increment(ref accepted);
				}
			}
			catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
			{
				// The listener was stopped; the test is over.
			}
		});

		using OllamaEmbeddingVectorizer vectorizer = new($"http://127.0.0.1:{port}/v1", Model, ModelVersionId, Dimension, Deadline);
		Func<Task> embed = async () => await vectorizer.VectorizeAsync(["text"], EmbeddingKind.Document);

		(await embed.Should().ThrowAsync<Exception>()).Which.Should().NotBeOfType<TimeoutException>("a dropped connection is a failure, not a deadline");
		listener.Stop();
		await accepting;

		accepted.Should().Be(1, "the probe and the dispatcher are the retry layers; the SDK's would stack a third underneath them");
	}

	/// <summary>Records what it was asked, and returns vectors of a width the test chooses.</summary>
	private sealed class FakeEmbeddingGenerator(Int32 width) : IEmbeddingGenerator<String, Embedding<Single>>
	{
		public IReadOnlyList<String> LastInputs { get; private set; } = [];

		public Int32 Calls { get; private set; }

		/// <summary>Caps how many embeddings come back, to stage a count mismatch.</summary>
		public Int32? EmitAtMost { get; init; }

		/// <summary>Stages a transport failure: an unreachable server, or one without the model.</summary>
		public Func<Exception>? ThrowWith { get; init; }

		/// <summary>Stages a server that never answers: the call returns only when its token is cancelled.</summary>
		public Boolean HangUntilCancelled { get; init; }

		public async Task<GeneratedEmbeddings<Embedding<Single>>> GenerateAsync(
			IEnumerable<String> values,
			EmbeddingGenerationOptions? options = null,
			CancellationToken cancellationToken = default)
		{
			this.Calls++;
			this.LastInputs = [.. values];

			if (this.HangUntilCancelled)
			{
				await Task.Delay(Timeout.Infinite, cancellationToken);
			}

			if (this.ThrowWith is not null)
			{
				throw this.ThrowWith();
			}

			Int32 count = Math.Min(this.LastInputs.Count, this.EmitAtMost ?? this.LastInputs.Count);

			GeneratedEmbeddings<Embedding<Single>> generated = [];

			for (Int32 i = 0; i < count; i++)
			{
				generated.Add(new Embedding<Single>(new Single[width]));
			}

			return generated;
		}

		public Object? GetService(Type serviceType, Object? serviceKey = null) => null;

		public void Dispose()
		{
		}
	}
}
