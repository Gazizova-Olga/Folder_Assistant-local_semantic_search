using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;

namespace FolderAssistant.Embedding;

/// <summary>
/// A real, local, semantic embedder: <c>qwen3-embedding:0.6b</c> served by a local Ollama process over
/// its OpenAI-compatible endpoint (<c>SPEC-162</c>). It is an <see cref="IVectorizer"/> and not an
/// <see cref="IFittableVectorizer"/> — the model is pretrained, so there is no corpus fit to compute or
/// persist, and unlike the LSA path a query can be embedded without restoring anything first.
///
/// <para>
/// Two properties of this model are load-bearing. It is <em>asymmetric</em>: the query side takes a Qwen3
/// instruction prefix and the document side is embedded verbatim, which maps exactly onto
/// <see cref="EmbeddingKind"/>. And its vector width is fixed by the weights, so the width is the
/// vectorizer's to report rather than configuration's to choose — this validates that the model emits the
/// composed width and fails loudly instead of storing a wrong-width vector into a
/// <c>(chunk_id, model_version_id)</c> space that assumes one width.
/// </para>
///
/// <para>
/// The transport is <c>Microsoft.Extensions.AI</c>'s embedding generator over the OpenAI client, pointed
/// at the local endpoint. That is a dependency this project did not previously have, and it is taken here
/// rather than pretended away: nothing else in the tree speaks to a model server yet. No request leaves
/// the machine — the endpoint is localhost — and the offline-by-construction guarantee the other profiles
/// carry is what this one trades for real semantics. The client retries nothing underneath a call and
/// carries the configured deadline as its own network timeout (<see cref="ClientOptions"/>): the startup
/// probe and the outbox dispatcher are the retry layers above it.
/// </para>
/// </summary>
internal sealed class OllamaEmbeddingVectorizer : IVectorizer, IEmbeddingHealthCheck, IDisposable
{
	/// <summary>
	/// Qwen3-Embedding applies an instruction to the query side only; documents are embedded verbatim.
	/// This is the model's own recommended retrieval instruction, and getting it wrong measurably degrades
	/// ranking rather than failing — which is why it is a constant here and not a setting.
	/// </summary>
	private const String QueryInstructionPrefix =
		"Instruct: Given a search query, retrieve relevant passages that answer the query\nQuery: ";

	/// <summary>
	/// A slow-starting server — the model paging in on first use — can refuse the first probe. A few
	/// quick retries separate "not up yet" from "not there at all", without turning a genuine outage
	/// into a long stall at startup.
	/// </summary>
	private const Int32 HealthCheckAttempts = 3;

	private static readonly TimeSpan HealthCheckRetryDelay = TimeSpan.FromSeconds(1);

	/// <summary>
	/// The probe's own bound, deliberately not the configured deadline. That deadline is sized for a full
	/// embed window of the slowest chunks a folder holds — 600 s by default — while the probe embeds two
	/// words, so sharing it would mean a server that accepts connections and never answers takes three
	/// attempts of ten minutes to be reported, with the index reading <c>Building</c> throughout. A server
	/// that is simply not there still fails in milliseconds on a refused connection; this bounds the case
	/// where something answers the socket and then says nothing.
	///
	/// <para>
	/// Thirty seconds rather than a second or two because the first call after a cold start pages the
	/// model in, which is the transient the retries exist for and must not be mistaken for an outage.
	/// </para>
	/// </summary>
	internal static readonly TimeSpan HealthCheckDeadline = TimeSpan.FromSeconds(30);

	private readonly IEmbeddingGenerator<String, Embedding<Single>> _generator;
	private readonly TimeSpan _timeout;
	private readonly TimeSpan _probeDeadline;

	/// <param name="timeout">
	/// The bound on one call, whatever its batch size. A call that never returns would otherwise hold a
	/// delivery in flight forever with its attempts still at zero — retry, backoff and the attempt limit
	/// never engage — and during the first pass would leave the index <c>Building</c> for the life of the
	/// process. Sized for a full embed window of the slowest chunks a folder holds: a chunk costs what its
	/// model tokens cost, measured here CPU-only at about 2.1 s for a full English window and 4.3 s for a
	/// Russian one, so a window of 64 is about 190 s and the configured default of 600 s leaves room for a
	/// slower machine while still ending a hung call inside one delivery's lifetime. The startup probe
	/// bounds itself far shorter (<see cref="HealthCheckDeadline"/>), because it embeds two words.
	/// </param>
	/// <param name="allowRemoteEndpoint">
	/// Whether <paramref name="endpoint"/> may name a host that is not on loopback. The check is made
	/// here, before a client exists, because this constructor is the narrowest point every composition
	/// path to a model server passes through: a profile that could be pointed at a remote host by a
	/// configuration value nobody checked is not the offline system this one claims to be
	/// (<c>EmbeddingEndpointGuard</c>).
	/// </param>
	public OllamaEmbeddingVectorizer(
		String endpoint,
		String model,
		String modelVersionId,
		Int32 dimension,
		TimeSpan timeout,
		Boolean allowRemoteEndpoint)
		: this(BuildGenerator(endpoint, model, timeout, allowRemoteEndpoint), model, modelVersionId, dimension, timeout)
	{
	}

	/// <summary>
	/// Test seam: takes a generator directly, so the suite needs no live server.
	/// <paramref name="probeDeadline"/> shortens <see cref="HealthCheckDeadline"/> so that a test of the
	/// probe's own bound does not have to wait three times thirty seconds to see it.
	/// </summary>
	internal OllamaEmbeddingVectorizer(
		IEmbeddingGenerator<String, Embedding<Single>> generator,
		String model,
		String modelVersionId,
		Int32 dimension,
		TimeSpan timeout,
		TimeSpan? probeDeadline = null)
	{
		ArgumentNullException.ThrowIfNull(generator);
		ArgumentException.ThrowIfNullOrWhiteSpace(model);
		ArgumentException.ThrowIfNullOrWhiteSpace(modelVersionId);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimension);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

		this._generator = generator;
		this._timeout = timeout;
		this._probeDeadline = probeDeadline ?? HealthCheckDeadline;
		this.Descriptor = new ModelDescriptor(modelVersionId, "ollama", model, dimension, "cosine");
	}

	public ModelDescriptor Descriptor { get; }

	public async ValueTask<IReadOnlyList<EmbeddingResult>> VectorizeAsync(
		IReadOnlyList<String> texts,
		EmbeddingKind kind,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(texts);

		if (texts.Count == 0)
		{
			// No call at all, rather than a round-trip that asks a model server to embed nothing.
			return [];
		}

		IReadOnlyList<String> inputs = kind == EmbeddingKind.Query
			? [.. texts.Select(static text => QueryInstructionPrefix + text)]
			: texts;

		// Bounded by this vectorizer's own deadline, linked to the caller's token. The deadline is
		// reported as a timeout and never as a cancellation: nobody cancelled, and telemetry filing it
		// as one would describe a shutdown where there was a hung server. The same conversion covers
		// the HTTP client's own deadline, which it reports as a cancellation too — the filter is the
		// caller's token, not the exception type.
		using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadline.CancelAfter(this._timeout);

		GeneratedEmbeddings<Embedding<Single>> generated;

		try
		{
			generated = await this._generator.GenerateAsync(inputs, options: null, deadline.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
		{
			throw new TimeoutException(
				$"Ollama did not return {texts.Count} embedding(s) for model '{this.Descriptor.ModelName}' within "
				+ $"{this._timeout.TotalSeconds:F0} s. The server is hung, overloaded, or still loading the model; "
				+ "the call is abandoned so that the work can be tried again rather than held open.",
				ex);
		}

		if (generated.Count != texts.Count)
		{
			throw new InvalidOperationException(
				$"Ollama returned {generated.Count} embeddings for {texts.Count} inputs "
				+ $"(model '{this.Descriptor.ModelName}'). Pairing them by position would mislabel every vector.");
		}

		EmbeddingResult[] results = new EmbeddingResult[texts.Count];

		for (Int32 i = 0; i < texts.Count; i++)
		{
			Single[] vector = generated[i].Vector.ToArray();

			if (vector.Length != this.Descriptor.Dimension)
			{
				throw new InvalidOperationException(
					$"Ollama model '{this.Descriptor.ModelName}' emitted a {vector.Length}-dimension vector, but the "
					+ $"composed dimension is {this.Descriptor.Dimension}. Set the configured dimension to the model's "
					+ "own width; storing a wrong-width vector would corrupt this model version's vector space, and "
					+ "every later query against it would compare against something it does not match.");
			}

			results[i] = new EmbeddingResult(
				vector, this.Descriptor.ModelVersionId, this.Descriptor.Dimension, this.Descriptor.ProviderType);
		}

		return results;
	}

	/// <summary>
	/// The pre-index probe. Embeds one throwaway document, which exercises the whole path at once:
	/// transport reachability, model presence, and output width.
	///
	/// <para>
	/// A width mismatch is passed straight through without retrying. It is a configuration error, not a
	/// transient one, and retrying it three times only delays the same answer. A probe that hits the
	/// deadline is retried like a refusal: a model still paging in on its first call is exactly the
	/// transient the retries exist for, and each attempt is bounded, so the probe cannot hang either.
	/// </para>
	/// </summary>
	public async ValueTask CheckAsync(CancellationToken cancellationToken = default)
	{
		for (Int32 attempt = 1; attempt <= HealthCheckAttempts; attempt++)
		{
			// The probe's own deadline, linked to the caller's: this call is two words, not a window, and
			// the configured deadline is sized for the window. Reaching it is a failed attempt like any
			// other — retried, and reported as unusable on the last one.
			using CancellationTokenSource probeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			probeDeadline.CancelAfter(this._probeDeadline);

			try
			{
				// Document, not Query: the probe should not prepend the model's retrieval instruction.
				_ = await this.VectorizeAsync(["health check"], EmbeddingKind.Document, probeDeadline.Token)
					.ConfigureAwait(false);

				return;
			}
			catch (InvalidOperationException)
			{
				// The vectorizer's own width or count validation. Non-transient — surface it as-is.
				throw;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				// The caller stopped, which is not this server's fault and is not an attempt.
				throw;
			}
			catch (Exception ex) when (ex is not OperationCanceledException || probeDeadline.IsCancellationRequested)
			{
				if (attempt >= HealthCheckAttempts)
				{
					// A cancelled probe says nothing useful in its own message ("The operation was
					// canceled"), so the reason is named here instead: a server that answered the socket
					// and then said nothing is a different fault from one that refused it.
					String reason = probeDeadline.IsCancellationRequested
						? $"the server did not answer a two-word probe within {this._probeDeadline.TotalSeconds:F0} s"
						: ex.Message;

					throw new InvalidOperationException(
						$"The Ollama embedding backend is not usable: a probe embed with model "
						+ $"'{this.Descriptor.ModelName}' failed {HealthCheckAttempts} times. Check that Ollama is "
						+ "running at the configured endpoint and that the model has been pulled. "
						+ $"Underlying error: {reason}",
						ex);
				}

				await Task.Delay(HealthCheckRetryDelay, cancellationToken).ConfigureAwait(false);
			}
		}
	}

	public void Dispose() => (this._generator as IDisposable)?.Dispose();

	/// <summary>
	/// The pipeline options the client is built with. Two of the SDK's defaults are overridden, and they go
	/// together. Its retry policy is off: the startup probe and the outbox dispatcher each retry above this
	/// call already, and the SDK's own retries underneath them cost a server that is not there four
	/// connections per attempt instead of one, reported as "Retry failed after 4 tries" rather than as the
	/// refusal. With nothing retrying under the call, the SDK's own network timeout — 100 s by default,
	/// whatever the host configures — would be the effective deadline for any configured value above it,
	/// so it is set to the configured one and one number bounds the call. Separate and internal so a test
	/// can hold the values rather than infer them from a hang.
	/// </summary>
	internal static OpenAIClientOptions ClientOptions(String endpoint, TimeSpan timeout)
		=> new()
		{
			Endpoint = new Uri(endpoint),
			NetworkTimeout = timeout,
			RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
		};

	private static IEmbeddingGenerator<String, Embedding<Single>> BuildGenerator(
		String endpoint,
		String model,
		TimeSpan timeout,
		Boolean allowRemoteEndpoint)
	{
		// Before anything is built: document text is what goes to this endpoint, so where it points is
		// checked rather than assumed (SPEC-162).
		EmbeddingEndpointGuard.Validate(endpoint, allowRemoteEndpoint);

		// Ollama authenticates nothing, but the OpenAI client requires a non-empty credential, so a
		// placeholder stands in. It is never sent anywhere that would check it.
		return new OpenAIClient(new ApiKeyCredential("ollama-local-no-key"), ClientOptions(endpoint, timeout))
			.GetEmbeddingClient(model)
			.AsIEmbeddingGenerator();
	}
}
