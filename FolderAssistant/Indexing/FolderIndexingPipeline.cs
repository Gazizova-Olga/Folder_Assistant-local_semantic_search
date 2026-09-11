using FolderAssistant.Embedding;
using FolderAssistant.Persistence;

namespace FolderAssistant.Indexing;

/// <summary>What one indexing pass got through.</summary>
internal sealed record IndexingResult(
	Int32 FilesScanned,
	Int32 FilesIndexed,
	Int32 ChunksIndexed,
	Int32 VectorsIndexed,
	Int32 FilesUnchanged,
	Int32 FilesDeleted);

/// <summary>
/// Scan, tokenize, chunk, embed, store — the whole pass, in that order, over the analyzed folder.
///
/// <para>
/// The stages are separate types so each is testable on its own: the chunker's window arithmetic and
/// the scanner's filtering are the two places an error is invisible from the outside, because a
/// wrongly-chunked corpus still indexes and still returns results.
/// </para>
/// </summary>
internal sealed class FolderIndexingPipeline
{
	private readonly LocalTextFileScanner _scanner = new();
	private readonly SimpleTokenizer _tokenizer = new();
	private readonly TextChunker _chunker = new();
	private readonly FolderIndexRepository _repository;
	private readonly IVectorizer? _vectorizer;
	private readonly IFolderManifestReader _manifestReader;
	private readonly IVectorStoreReader _vectorStoreReader;

	public FolderIndexingPipeline()
		: this(null)
	{
	}

	/// <summary>Composition-time override; with none, the programmable baseline is built from config.</summary>
	internal FolderIndexingPipeline(IVectorizer? vectorizer)
		: this(vectorizer, new SqliteBlobVectorStoreWriter(), new SqliteBlobVectorStoreReader())
	{
	}

	/// <summary>
	/// The vector store is supplied as a matched writer/reader pair, and they have to agree.
	///
	/// <para>
	/// A reader looking in <c>chunk_vector</c> while the writer fills a <c>vec0</c> virtual table would
	/// report that nothing has ever been embedded, so every file would be re-embedded on every run —
	/// with the index still looking correct from the outside.
	/// </para>
	/// </summary>
	internal FolderIndexingPipeline(
		IVectorizer? vectorizer,
		IVectorStoreWriter vectorStoreWriter,
		IVectorStoreReader vectorStoreReader)
		: this(vectorizer, new SqliteFolderManifestReader(vectorStoreReader), vectorStoreWriter, vectorStoreReader)
	{
	}

	internal FolderIndexingPipeline(
		IVectorizer? vectorizer,
		IFolderManifestReader manifestReader,
		IVectorStoreWriter vectorStoreWriter,
		IVectorStoreReader vectorStoreReader)
	{
		ArgumentNullException.ThrowIfNull(manifestReader);
		ArgumentNullException.ThrowIfNull(vectorStoreWriter);
		ArgumentNullException.ThrowIfNull(vectorStoreReader);

		this._vectorizer = vectorizer;
		this._manifestReader = manifestReader;
		this._vectorStoreReader = vectorStoreReader;
		this._repository = new FolderIndexRepository(vectorStoreWriter);
	}

	/// <summary>
	/// Streams the folder: each file is read, chunked, embedded if it changed, and its text then
	/// dropped before the next one is pulled.
	///
	/// <para>
	/// What accumulates is only what the write needs — file metadata, chunk metadata and vectors.
	/// Text is never held across files. Buffering every file's content and every chunk's content for
	/// the whole corpus, as this did before, is what set the practical ceiling on folder size: the
	/// text alone doubles in memory because .NET strings are UTF-16, and chunk overlap duplicates
	/// part of it again (<c>SPEC-120</c>).
	/// </para>
	///
	/// <para>
	/// The write is still a single transaction. Streaming changed what is held in memory, not the
	/// atomicity of what is stored.
	/// </para>
	/// </summary>
	public IndexingResult Run(String analyzedFolderPath, String databasePath, IndexingConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);

		IVectorizer vectorizer = this._vectorizer
			?? new ProgrammableEmbeddingVectorizer(config.ModelVersionId, config.VectorDimension);

		// Resolving the fit first is what decides whether text can be streamed at all. A
		// corpus-fitted vectorizer with no fit yet cannot embed anything until it has seen every
		// chunk, so that one case has to keep chunk text alive across the loop.
		Boolean mustFit = this.ResolveFit(vectorizer, databasePath);

		// Read against the model version, which a fit does not change — only the dimension moves,
		// and that is read back afterwards.
		IReadOnlyDictionary<String, IndexedFileState> knownFiles =
			this._manifestReader.ReadFileStates(databasePath, vectorizer.Descriptor.ModelVersionId);

		List<ScannedFile> files = [];
		Dictionary<String, IReadOnlyList<ChunkMetadata>> chunksByFile = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<String, EmbeddingResult> embeddingsByChunk = new(StringComparer.OrdinalIgnoreCase);

		// Non-null only while a fit is owed. Otherwise chunk text dies with each iteration.
		List<(IReadOnlyList<TextChunk> Chunks, Boolean Changed)>? awaitingFit = mustFit ? [] : null;

		Int32 scanned = 0;
		Int32 unchanged = 0;
		Int32 indexed = 0;

		foreach (ScannedTextFile file in this._scanner.Enumerate(analyzedFolderPath, config.MaxTextFileSizeBytes))
		{
			scanned++;

			// Every file is chunked, every pass. The scanner has already read it and chunking is
			// cheap; embedding is the expensive stage and the only one worth skipping.
			TokenizedText tokens = this._tokenizer.Tokenize(file.Content);

			IReadOnlyList<TextChunk> chunks = this._chunker.Chunk(
				file.FileId, tokens, config.ChunkSizeTokens, config.ChunkOverlapTokens);

			files.Add(file.ToMetadata());
			chunksByFile[file.FileId] = chunks.Select(static chunk => chunk.ToMetadata()).ToArray();

			if (chunks.Count == 0)
			{
				continue;
			}

			Boolean changed = !IsUnchanged(file, knownFiles);
			if (!changed)
			{
				unchanged++;
			}

			if (mustFit)
			{
				// The fit needs every chunk, changed or not, so this text has to outlive the loop.
				awaitingFit!.Add((chunks, changed));

				continue;
			}

			if (changed)
			{
				Embed(embeddingsByChunk, vectorizer, chunks);
				indexed++;
			}

			// Nothing above still references file.Content or the chunk text: both are collectable
			// before the next file is pulled, which is the whole point of the loop's shape.
		}

		String? fitArtifactJson = null;

		if (awaitingFit is { Count: > 0 })
		{
			String[] corpus = awaitingFit
				.SelectMany(static entry => entry.Chunks)
				.Select(static chunk => chunk.Content)
				.ToArray();

			fitArtifactJson = ((IFittableVectorizer)vectorizer).Fit(corpus);

			foreach ((IReadOnlyList<TextChunk> chunks, Boolean changed) in awaitingFit)
			{
				if (!changed)
				{
					continue;
				}

				Embed(embeddingsByChunk, vectorizer, chunks);
				indexed++;
			}

			awaitingFit.Clear();
		}

		// Read after fitting: the descriptor's dimension is the rank actually reached, not the one
		// that was requested.
		ModelDescriptor descriptor = vectorizer.Descriptor;

		IndexWriteSummary summary = this._repository.Upsert(
			databasePath,
			files,
			chunksByFile,
			embeddingsByChunk,
			descriptor,
			fitArtifactJson);

		// A whole-folder pass is the largest single write this system makes, and it ends here: fold the log
		// back into the database once, rather than leaving the folder holding it until a later writer happens
		// to reclaim it.
		FolderDatabaseMaintenance.Checkpoint(databasePath);

		return new IndexingResult(
			FilesScanned: scanned,
			FilesIndexed: indexed,
			ChunksIndexed: summary.ChunksUpserted,
			VectorsIndexed: summary.VectorsUpserted,
			FilesUnchanged: unchanged,
			FilesDeleted: summary.FilesDeleted);
	}

	private static void Embed(
		Dictionary<String, EmbeddingResult> target,
		IVectorizer vectorizer,
		IReadOnlyList<TextChunk> chunks)
	{
		String[] texts = new String[chunks.Count];
		for (Int32 i = 0; i < chunks.Count; i++)
		{
			texts[i] = chunks[i].Content;
		}

		IReadOnlyList<EmbeddingResult> vectors = vectorizer.Vectorize(texts, EmbeddingKind.Document);
		for (Int32 i = 0; i < chunks.Count; i++)
		{
			target[chunks[i].ChunkId] = vectors[i];
		}
	}

	/// <summary>
	/// A file is skippable only when its content is unchanged <em>and</em> the active model has
	/// already embedded it.
	///
	/// <para>
	/// Content equality alone is not sufficient. After switching embedding implementation every file
	/// is unchanged, yet none of them has a vector in the new model's space — so skipping on the hash
	/// alone would leave the new model with a silently empty index.
	/// </para>
	/// </summary>
	private static Boolean IsUnchanged(
		ScannedTextFile file,
		IReadOnlyDictionary<String, IndexedFileState> knownFiles)
		=> knownFiles.TryGetValue(file.FileId, out IndexedFileState? known)
			&& known.HasVectorsForModel
			&& String.Equals(known.FileHash, file.FileHash, StringComparison.Ordinal);

	/// <summary>
	/// Resolves the fit for a corpus-fitted vectorizer, and reports whether one is still owed.
	///
	/// <para>
	/// An existing artifact is <b>reused, not recomputed</b>. Refitting changes the projection and
	/// would invalidate every vector already stored under this model version, so it stays a
	/// deliberate act under a new one rather than something an ordinary edit triggers.
	/// </para>
	///
	/// <para>
	/// The accepted cost: incremental updates embed against the corpus as it was when the fit was
	/// taken, so vocabulary and inverse document frequencies drift as the folder changes. Nothing
	/// detects that drift.
	/// </para>
	/// </summary>
	private Boolean ResolveFit(IVectorizer vectorizer, String databasePath)
	{
		if (vectorizer is not IFittableVectorizer fittable)
		{
			return false;
		}

		String? existing = this._vectorStoreReader.ReadFitArtifact(
			databasePath, vectorizer.Descriptor.ModelVersionId);

		if (existing is null)
		{
			return true;
		}

		fittable.LoadFit(existing);

		return false;
	}
}
