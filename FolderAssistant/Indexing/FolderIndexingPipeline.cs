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
	private readonly FolderIndexRepository _repository = new();
	private readonly IVectorizer? _vectorizer;
	private readonly IFolderManifestReader _manifestReader;
	private readonly IVectorStoreReader _vectorStoreReader;

	public FolderIndexingPipeline()
		: this(null)
	{
	}

	/// <summary>Composition-time override; with none, the programmable baseline is built from config.</summary>
	internal FolderIndexingPipeline(IVectorizer? vectorizer)
		: this(vectorizer, new SqliteFolderManifestReader(), new SqliteJsonVectorStoreReader())
	{
	}

	internal FolderIndexingPipeline(
		IVectorizer? vectorizer,
		IFolderManifestReader manifestReader,
		IVectorStoreReader vectorStoreReader)
	{
		ArgumentNullException.ThrowIfNull(manifestReader);
		ArgumentNullException.ThrowIfNull(vectorStoreReader);

		this._vectorizer = vectorizer;
		this._manifestReader = manifestReader;
		this._vectorStoreReader = vectorStoreReader;
	}

	public IndexingResult Run(String analyzedFolderPath, String databasePath, IndexingConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);

		IVectorizer vectorizer = this._vectorizer
			?? new ProgrammableEmbeddingVectorizer(config.ModelVersionId, config.VectorDimension);

		IReadOnlyList<ScannedTextFile> files = this._scanner.Scan(analyzedFolderPath, config.MaxTextFileSizeBytes);

		// Everything is chunked, every pass. The scanner has already read the content, chunking is
		// cheap, and a corpus-fitted vectorizer needs the full chunk set regardless. Embedding is the
		// expensive stage and the only one skipped — which is what makes a restart over an unchanged
		// folder cheap.
		Dictionary<String, IReadOnlyList<TextChunk>> chunksByFile = new(StringComparer.OrdinalIgnoreCase);

		foreach (ScannedTextFile file in files)
		{
			TokenizedText tokens = this._tokenizer.Tokenize(file.Content);

			chunksByFile[file.FileId] = this._chunker.Chunk(
				file.FileId, tokens.Tokens, config.ChunkSizeTokens, config.ChunkOverlapTokens);
		}

		ModelDescriptor descriptor = this.PrepareVectorizer(
			vectorizer, files, chunksByFile, databasePath, out String? fitArtifactJson);

		IReadOnlyDictionary<String, IndexedFileState> knownFiles =
			this._manifestReader.ReadFileStates(databasePath, descriptor.ModelVersionId);

		Dictionary<String, EmbeddingResult> embeddingsByChunk = new(StringComparer.OrdinalIgnoreCase);
		Int32 unchanged = 0;
		Int32 indexed = 0;

		foreach (ScannedTextFile file in files)
		{
			IReadOnlyList<TextChunk> chunks = chunksByFile[file.FileId];

			if (chunks.Count == 0)
			{
				continue;
			}

			if (IsUnchanged(file, knownFiles))
			{
				unchanged++;
				continue;
			}

			IReadOnlyList<EmbeddingResult> vectors = vectorizer.Vectorize(
				chunks.Select(static chunk => chunk.Content).ToArray(),
				EmbeddingKind.Document);

			for (Int32 i = 0; i < chunks.Count; i++)
			{
				embeddingsByChunk[chunks[i].ChunkId] = vectors[i];
			}

			indexed++;
		}

		IndexWriteSummary summary = this._repository.Upsert(
			databasePath,
			files,
			chunksByFile,
			embeddingsByChunk,
			descriptor,
			fitArtifactJson);

		return new IndexingResult(
			FilesScanned: files.Count,
			FilesIndexed: indexed,
			ChunksIndexed: summary.ChunksUpserted,
			VectorsIndexed: summary.VectorsUpserted,
			FilesUnchanged: unchanged,
			FilesDeleted: summary.FilesDeleted);
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
	/// Resolves the fit for a corpus-fitted vectorizer.
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
	private ModelDescriptor PrepareVectorizer(
		IVectorizer vectorizer,
		IReadOnlyList<ScannedTextFile> files,
		IReadOnlyDictionary<String, IReadOnlyList<TextChunk>> chunksByFile,
		String databasePath,
		out String? fitArtifactJson)
	{
		fitArtifactJson = null;

		if (vectorizer is not IFittableVectorizer fittable)
		{
			return vectorizer.Descriptor;
		}

		String? existing = this._vectorStoreReader.ReadFitArtifact(
			databasePath, vectorizer.Descriptor.ModelVersionId);

		if (existing is not null)
		{
			fittable.LoadFit(existing);

			return vectorizer.Descriptor;
		}

		String[] corpus = files
			.SelectMany(file => chunksByFile[file.FileId])
			.Select(static chunk => chunk.Content)
			.ToArray();

		if (corpus.Length == 0)
		{
			return vectorizer.Descriptor;
		}

		fitArtifactJson = fittable.Fit(corpus);

		// Read after fitting: the descriptor's dimension is the rank actually reached, not the one
		// that was requested.
		return vectorizer.Descriptor;
	}
}
