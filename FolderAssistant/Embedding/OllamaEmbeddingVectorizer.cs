using System.ClientModel;
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
/// carry is what this one trades for real semantics.
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

	private readonly IEmbeddingGenerator<String, Embedding<Single>> _generator;

	public OllamaEmbeddingVectorizer(String endpoint, String model, String modelVersionId, Int32 dimension)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
		ArgumentException.ThrowIfNullOrWhiteSpace(model);
		ArgumentException.ThrowIfNullOrWhiteSpace(modelVersionId);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimension);

		this._generator = BuildGenerator(endpoint, model);
		this.Descriptor = new ModelDescriptor(modelVersionId, "ollama", model, dimension, "cosine");
	}

	/// <summary>Test seam: takes a generator directly, so the suite needs no live server.</summary>
	internal OllamaEmbeddingVectorizer(
		IEmbeddingGenerator<String, Embedding<Single>> generator,
		String model,
		String modelVersionId,
		Int32 dimension)
	{
		ArgumentNullException.ThrowIfNull(generator);

		this._generator = generator;
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

		GeneratedEmbeddings<Embedding<Single>> generated =
			await this._generator.GenerateAsync(inputs, options: null, cancellationToken).ConfigureAwait(false);

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
	/// transient one, and retrying it three times only delays the same answer.
	/// </para>
	/// </summary>
	public async ValueTask CheckAsync(CancellationToken cancellationToken = default)
	{
		for (Int32 attempt = 1; attempt <= HealthCheckAttempts; attempt++)
		{
			try
			{
				// Document, not Query: the probe should not prepend the model's retrieval instruction.
				_ = await this.VectorizeAsync(["health check"], EmbeddingKind.Document, cancellationToken)
					.ConfigureAwait(false);

				return;
			}
			catch (InvalidOperationException)
			{
				// The vectorizer's own width or count validation. Non-transient — surface it as-is.
				throw;
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				if (attempt >= HealthCheckAttempts)
				{
					throw new InvalidOperationException(
						$"The Ollama embedding backend is not usable: a probe embed with model "
						+ $"'{this.Descriptor.ModelName}' failed {HealthCheckAttempts} times. Check that Ollama is "
						+ "running at the configured endpoint and that the model has been pulled. "
						+ $"Underlying error: {ex.Message}",
						ex);
				}

				await Task.Delay(HealthCheckRetryDelay, cancellationToken).ConfigureAwait(false);
			}
		}
	}

	public void Dispose() => (this._generator as IDisposable)?.Dispose();

	private static IEmbeddingGenerator<String, Embedding<Single>> BuildGenerator(String endpoint, String model)
	{
		// Ollama authenticates nothing, but the OpenAI client requires a non-empty credential, so a
		// placeholder stands in. It is never sent anywhere that would check it.
		OpenAIClientOptions options = new() { Endpoint = new Uri(endpoint) };

		return new OpenAIClient(new ApiKeyCredential("ollama-local-no-key"), options)
			.GetEmbeddingClient(model)
			.AsIEmbeddingGenerator();
	}
}
