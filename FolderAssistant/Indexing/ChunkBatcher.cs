using FolderAssistant.Embedding;

namespace FolderAssistant.Indexing;

/// <summary>
/// Gathers chunks and embeds them a fixed window at a time.
///
/// <para>
/// The window is a <em>memory</em> bound before it is anything else. Each chunk is embedded
/// independently of the others in its call, so where the window happens to cut changes nothing that is
/// stored — which is exactly what lets it cut at a fixed count rather than on a file boundary, and so
/// caps the chunk text held at one window's worth however large the folder or the file.
/// </para>
///
/// <para>
/// It buys fewer calls, and measurably no speed: against a local embedding server, windows of 1, 8 and
/// 64 index the same corpus in indistinguishable time, because what is paid for is the model's
/// inference rather than the round trip (<c>SPEC-120</c>). A backend that embedded a batch in parallel
/// would be a different measurement.
/// </para>
///
/// <para>
/// Shared by the corpus pass and the per-file delivery path deliberately. The bound is one decision,
/// and two copies of it would eventually disagree about how much text may be alive at once — which is
/// the kind of difference nothing would report.
/// </para>
/// </summary>
internal sealed class ChunkBatcher
{
	private readonly IVectorizer _vectorizer;
	private readonly IDictionary<String, EmbeddingResult> _target;
	private readonly Int32 _windowSize;
	private readonly List<TextChunk> _pending;

	public ChunkBatcher(IVectorizer vectorizer, IDictionary<String, EmbeddingResult> target, Int32 windowSize)
	{
		ArgumentNullException.ThrowIfNull(vectorizer);
		ArgumentNullException.ThrowIfNull(target);

		this._vectorizer = vectorizer;
		this._target = target;

		// Below one is meaningless rather than dangerous, and it is worth being exact about which: the
		// flush test fires as soon as anything is pending, so a zero would already behave as a one. What
		// the clamp actually prevents is a negative reaching the capacity argument below, which throws.
		this._windowSize = Math.Max(1, windowSize);
		this._pending = new List<TextChunk>(this._windowSize);
	}

	public void Add(IReadOnlyList<TextChunk> chunks)
	{
		for (Int32 i = 0; i < chunks.Count; i++)
		{
			this._pending.Add(chunks[i]);

			if (this._pending.Count >= this._windowSize)
			{
				this.Flush();
			}
		}
	}

	public void Flush()
	{
		if (this._pending.Count == 0)
		{
			return;
		}

		String[] texts = new String[this._pending.Count];
		for (Int32 i = 0; i < this._pending.Count; i++)
		{
			texts[i] = this._pending[i].Content;
		}

		IReadOnlyList<EmbeddingResult> vectors = this._vectorizer.Vectorize(texts, EmbeddingKind.Document);
		for (Int32 i = 0; i < this._pending.Count; i++)
		{
			this._target[this._pending[i].ChunkId] = vectors[i];
		}

		this._pending.Clear();
	}
}
