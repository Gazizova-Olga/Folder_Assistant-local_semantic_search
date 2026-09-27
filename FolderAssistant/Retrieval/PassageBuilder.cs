using System.Text;
using FolderAssistant.Extraction;
using FolderAssistant.Indexing;

namespace FolderAssistant.Retrieval;

/// <summary>What a rebuilt passage is worth (SPEC-110).</summary>
internal enum PassageState
{
	/// <summary>The rebuilt window hashes to the chunk's recorded hash: this is the text that was embedded.</summary>
	Verified,

	/// <summary>The file is there but the window no longer hashes to the chunk: the file changed after it was indexed.</summary>
	Stale,

	/// <summary>The file is gone, cannot be read, or has no extractor.</summary>
	Unavailable,
}

/// <summary>A hit with its passage rebuilt from disk. <see cref="Text"/> is set only when <see cref="State"/> is <see cref="PassageState.Verified"/>.</summary>
internal sealed record RebuiltPassage(RetrievalHit Hit, PassageState State, String? Text)
{
	/// <summary>The chunk's length in the chunker's own tokens — the unit the context budget is spent in.</summary>
	public Int32 TokenCount => this.Hit.TokenEnd - this.Hit.TokenStart;
}

/// <summary>
/// Rebuilds a hit's passage from the file it came from, and refuses to hand back one that is not the
/// text that was embedded.
///
/// <para>
/// Chunks store no text (SPEC-120): a hit is a file path and a token window, and the passage is the
/// file re-read now, tokenized the way the chunker tokenized it, and the window rejoined the way the
/// chunker joined it. Nothing about that says the file is still the file that was embedded. An edit
/// inside the settle window, or a file whose re-delivery was abandoned, yields a window of the wrong
/// text under a real path and a real score — the silently plausible wrong answer this tree exists to
/// refuse — and it is also the exact defect that keeps container formats out of the extractor
/// registry, since a window cut from a ZIP's bytes looks like nothing at all.
/// </para>
///
/// <para>
/// So the rebuilt window is hashed <b>before anything else is done with it</b> — the same SHA-256 over
/// the same single-space join the chunker recorded as <c>chunk_hash</c> — and a mismatch withholds the
/// text rather than marking it. A stale passage carries its hit and its state, so a caller can say that
/// the file changed since it was indexed; it does not carry text a model could quote.
/// </para>
/// </summary>
internal sealed class PassageBuilder
{
	private readonly String _rootPath;
	private readonly TextExtractorRegistry _extractors;
	private readonly SimpleTokenizer _tokenizer = new();

	public PassageBuilder(String rootPath, TextExtractorRegistry extractors)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
		ArgumentNullException.ThrowIfNull(extractors);
		this._rootPath = Path.GetFullPath(rootPath);
		this._extractors = extractors;
	}

	/// <summary>
	/// Every hit's passage, in the order given. A file is read and tokenized once however many hits it
	/// holds, because a search over-fetches and a file's best passages sit together.
	/// </summary>
	public IReadOnlyList<RebuiltPassage> Rebuild(IReadOnlyList<RetrievalHit> hits)
	{
		ArgumentNullException.ThrowIfNull(hits);

		Dictionary<String, TokenizedText?> files = new(StringComparer.Ordinal);
		List<RebuiltPassage> passages = new(hits.Count);

		foreach (RetrievalHit hit in hits)
		{
			if (!files.TryGetValue(hit.FilePath, out TokenizedText? tokenized))
			{
				tokenized = this.Read(hit.FilePath);
				files[hit.FilePath] = tokenized;
			}

			passages.Add(Rebuild(hit, tokenized));
		}

		return passages;
	}

	public RebuiltPassage Rebuild(RetrievalHit hit)
	{
		ArgumentNullException.ThrowIfNull(hit);

		return Rebuild(hit, this.Read(hit.FilePath));
	}

	private static RebuiltPassage Rebuild(RetrievalHit hit, TokenizedText? tokenized)
	{
		if (tokenized is null)
		{
			return new RebuiltPassage(hit, PassageState.Unavailable, null);
		}

		// A window past the end of what the file now holds is the file having shrunk: stale, not an error.
		IReadOnlyList<TokenSpan> tokens = tokenized.Tokens;
		if (hit.TokenStart < 0 || hit.TokenEnd > tokens.Count || hit.TokenStart >= hit.TokenEnd)
		{
			return new RebuiltPassage(hit, PassageState.Stale, null);
		}

		StringBuilder builder = new();
		for (Int32 i = hit.TokenStart; i < hit.TokenEnd; i++)
		{
			if (i > hit.TokenStart)
			{
				builder.Append(' ');
			}

			builder.Append(tokenized.Source.AsSpan(tokens[i].Start, tokens[i].Length));
		}

		String text = builder.ToString();

		return String.Equals(TextChunker.Sha256(text), hit.ChunkHash, StringComparison.Ordinal)
			? new RebuiltPassage(hit, PassageState.Verified, text)
			: new RebuiltPassage(hit, PassageState.Stale, null);
	}

	/// <summary>
	/// Whether the folder still holds the file a hit names, as the index stores its path. Here rather
	/// than at a caller because this type owns the join from a stored relative path to a file on disk —
	/// a second copy of that join is a second way to be wrong about which file a hit means.
	///
	/// <para>
	/// This asks only whether the file is there, never whether it still says what was indexed. That
	/// question is <see cref="Rebuild"/>'s, and answering it costs a read and a hash per passage.
	/// </para>
	/// </summary>
	public Boolean Holds(String relativePath)
	{
		ArgumentNullException.ThrowIfNull(relativePath);

		return File.Exists(FullPath(this._rootPath, relativePath));
	}

	private static String FullPath(String rootPath, String relativePath)
		=> Path.GetFullPath(Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));

	/// <summary>
	/// The file as the indexer would read it now: bytes through the registry's extractor for its
	/// extension, tokenized by the chunker's tokenizer. Null when it cannot be — gone, locked by a writer,
	/// or a format the registry does not hold. Share-read and no retry, as every reader here: a failing
	/// read means a live writer, and the passage is unavailable rather than a moment late.
	/// </summary>
	private TokenizedText? Read(String relativePath)
	{
		String fullPath = FullPath(this._rootPath, relativePath);
		ITextExtractor? extractor = this._extractors.Find(Path.GetExtension(fullPath));
		if (extractor is null)
		{
			return null;
		}

		try
		{
			Byte[] bytes;
			using (FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
			{
				bytes = new Byte[stream.Length];
				stream.ReadExactly(bytes);
			}

			return this._tokenizer.Tokenize(extractor.Extract(bytes));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}
}
