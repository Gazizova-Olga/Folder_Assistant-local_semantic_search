using FolderAssistant.Embedding;
using FolderAssistant.Extraction;
using FolderAssistant.Indexing.Scanning;
using FolderAssistant.Indexing.Watching;
using FolderAssistant.Persistence;

namespace FolderAssistant.Indexing;

/// <summary>What one whole-folder pass got through.</summary>
/// <param name="FilesScanned">Files the scanner read.</param>
/// <param name="FilesIndexed">Files this pass embedded and marked delivered.</param>
/// <param name="ChunksIndexed">Chunk rows written for those files.</param>
/// <param name="VectorsIndexed">Vectors written for those files.</param>
/// <param name="FilesUnchanged">Files the active model had already embedded at this content.</param>
/// <param name="FilesDeferred">
/// Files left to the front end: read by the scanner but not recorded by the comparison that ran
/// first, or recorded at other content than the scanner read. Both are files in flux, and the
/// deliveries already queued for them are what index them.
/// </param>
/// <param name="ChangesRecorded">
/// What the comparison at the start of the pass recorded and queued — added, modified and removed
/// files together.
/// </param>
internal sealed record IndexingResult(
	Int32 FilesScanned,
	Int32 FilesIndexed,
	Int32 ChunksIndexed,
	Int32 VectorsIndexed,
	Int32 FilesUnchanged,
	Int32 FilesDeferred,
	Int32 ChangesRecorded);

/// <summary>
/// The whole-folder pass: record, then fit, then embed everything the record says lacks vectors.
///
/// <para>
/// It runs once, before the front end's loops, and it does two things one file at a time cannot: fit
/// a corpus-fitted embedder, which has to see every chunk before it can embed any, and embed a cold
/// folder in batched windows rather than one delivery per file.
/// </para>
///
/// <para>
/// <strong>It writes no file's record.</strong> The folder is recorded first, through the front end's
/// own comparison (<see cref="Reconciler"/>) into the store that owns <c>file_manifest</c>, so every
/// row — its hash, its creation time, its queued delivery — comes from the one classifier the front
/// end uses afterwards. This pass then embeds only what it can see recorded, at the content it read,
/// and reports what it embedded through the store's conditional delivery mark: the deliveries the
/// comparison queued find their work already done and skip. A file the comparison did not record, or
/// recorded at other content than the scanner read, is in flux and is left to the delivery queued for
/// it. Removals are likewise recorded and queued by the comparison and delivered by the dispatcher —
/// this pass deletes nothing, because only a delivered removal can clear a file's vectors before its
/// row (<c>SPEC-121</c>).
/// </para>
///
/// <para>
/// The stages are separate types so each is testable on its own: the chunker's window arithmetic and
/// the scanner's filtering are the two places an error is invisible from the outside, because a
/// wrongly-chunked corpus still indexes and still returns results.
/// </para>
/// </summary>
internal sealed class FolderIndexingPipeline
{
	private readonly TextExtractorRegistry _extractors;
	private readonly LocalTextFileScanner _scanner;
	private readonly SimpleTokenizer _tokenizer = new();
	private readonly TextChunker _chunker = new();
	private readonly FolderIndexRepository _repository;
	private readonly IVectorizer? _vectorizer;
	private readonly IFolderManifestReader _manifestReader;
	private readonly IVectorStoreReader _vectorStoreReader;
	private readonly String _metadataFolderName;

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
	/// <param name="metadataFolderName">
	/// The index's own folder, by its configured name, which the comparison at the start of a pass
	/// must not record. Null takes the configured default.
	/// </param>
	/// <param name="extractors">
	/// The one source of which files are read and how each is decoded. Null takes the registry the
	/// application runs with; the composition root passes its registered instance.
	/// </param>
	internal FolderIndexingPipeline(
		IVectorizer? vectorizer,
		IVectorStoreWriter vectorStoreWriter,
		IVectorStoreReader vectorStoreReader,
		String? metadataFolderName = null,
		TextExtractorRegistry? extractors = null)
		: this(vectorizer, new SqliteFolderManifestReader(vectorStoreReader), vectorStoreWriter, vectorStoreReader, metadataFolderName, extractors)
	{
	}

	internal FolderIndexingPipeline(
		IVectorizer? vectorizer,
		IFolderManifestReader manifestReader,
		IVectorStoreWriter vectorStoreWriter,
		IVectorStoreReader vectorStoreReader,
		String? metadataFolderName = null,
		TextExtractorRegistry? extractors = null)
	{
		ArgumentNullException.ThrowIfNull(manifestReader);
		ArgumentNullException.ThrowIfNull(vectorStoreWriter);
		ArgumentNullException.ThrowIfNull(vectorStoreReader);

		this._extractors = extractors ?? TextExtractorRegistry.Default;
		this._scanner = new LocalTextFileScanner(this._extractors);
		this._vectorizer = vectorizer;
		this._manifestReader = manifestReader;
		this._vectorStoreReader = vectorStoreReader;
		this._repository = new FolderIndexRepository(vectorStoreWriter);
		this._metadataFolderName = metadataFolderName ?? new PersistenceConfig().MetadataFolderName;
	}

	/// <summary>
	/// Records the folder, then streams it: each file is read, chunked, embedded if the record says it
	/// lacks vectors, and its text then dropped before the next one is pulled.
	///
	/// <para>
	/// What accumulates is only what the write needs — chunk metadata and vectors for the files being
	/// embedded, and the path and hash of each so it can be marked delivered. Text is never held
	/// across files. Buffering every file's content and every chunk's content for the whole corpus, as
	/// this did before, is what set the practical ceiling on folder size: the text alone doubles in
	/// memory because .NET strings are UTF-16, and chunk overlap duplicates part of it again
	/// (<c>SPEC-120</c>).
	/// </para>
	///
	/// <para>
	/// The chunks and vectors land in a single transaction, and the delivery marks in one more,
	/// afterwards. That order is the safe one: a mark without vectors is a file believed indexed that
	/// is not, where vectors without a mark cost one duplicate delivery.
	/// </para>
	/// </summary>
	public IndexingResult Run(String analyzedFolderPath, String databasePath, IndexingConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);

		IVectorizer vectorizer = this._vectorizer
			?? new ProgrammableEmbeddingVectorizer(config.ModelVersionId, config.VectorDimension);

		// Record first. Every row this pass will embed against is written here, by the store, from the
		// same comparison the front end runs — and nothing else is running yet, so nothing races it.
		FolderIndexStore store = new(databasePath);
		ReconcileResult recorded = this.Record(analyzedFolderPath, store, config);

		// What the comparison just recorded as added or modified. A file it recorded at a new hash may
		// well have vectors already — for the content before the edit — so the vector check alone would
		// call it unchanged. The comparison's own conclusion is what says otherwise.
		HashSet<String> changedThisPass = recorded.Changes
			.Where(static change => change.Delta != FileDelta.Removed)
			.Select(static change => FileIdentity.For(change.RelativePath))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		// Resolving the fit next is what decides whether text can be streamed at all. A corpus-fitted
		// vectorizer with no fit yet cannot embed anything until it has seen every chunk, so that one
		// case has to keep chunk text alive across the loop.
		Boolean mustFit = this.ResolveFit(vectorizer, databasePath);

		// Read against the model version, which a fit does not change — only the dimension moves,
		// and that is read back afterwards.
		IReadOnlyDictionary<String, IndexedFileState> knownFiles =
			this._manifestReader.ReadFileStates(databasePath, vectorizer.Descriptor.ModelVersionId);

		Dictionary<String, IReadOnlyList<ChunkMetadata>> chunksByFile = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<String, EmbeddingResult> embeddingsByChunk = new(StringComparer.OrdinalIgnoreCase);
		List<DeliveredContent> delivered = [];

		// Chunks are gathered across files and embedded a window at a time rather than a file at a time.
		// A file's chunks need not land in one call — each chunk embeds independently of the others in its
		// call — so the window is free to cut wherever it reaches its size, and that is what bounds how much
		// chunk text is alive at once.
		ChunkBatcher batcher = new(vectorizer, embeddingsByChunk, config.EmbeddingBatchSizeChunks);

		// Non-null only while a fit is owed. Otherwise chunk text dies with each iteration. The fit takes
		// every chunk the scanner produced, embedded or not.
		List<(ScannedTextFile File, IReadOnlyList<TextChunk> Chunks, Disposition Disposition)>? awaitingFit =
			mustFit ? [] : null;

		Int32 scanned = 0;
		Int32 unchanged = 0;
		Int32 deferred = 0;
		Int32 indexed = 0;

		void Place(ScannedTextFile file, IReadOnlyList<TextChunk> chunks, Disposition disposition)
		{
			switch (disposition)
			{
				case Disposition.Embed:
					chunksByFile[file.FileId] = chunks.Select(static chunk => chunk.ToMetadata()).ToArray();
					delivered.Add(new DeliveredContent(file.RelativePath, file.FileHash));
					batcher.Add(chunks);
					indexed++;
					break;

				case Disposition.Unchanged:
					// Chunk rows are rewritten for a file that is not re-embedded. The write is idempotent
					// for content-addressed ids, and it is what reconciles away a chunk row the current
					// chunker would not produce — a stale trailing window from an older chunker, say —
					// without an embed (SPEC-120, superseded chunks).
					chunksByFile[file.FileId] = chunks.Select(static chunk => chunk.ToMetadata()).ToArray();
					unchanged++;
					break;

				default:
					deferred++;
					break;
			}
		}

		foreach (ScannedTextFile file in this._scanner.Enumerate(analyzedFolderPath, config.MaxTextFileSizeBytes))
		{
			scanned++;

			// Every file is chunked, every pass. The scanner has already read it and chunking is
			// cheap; embedding is the expensive stage and the only one worth skipping.
			TokenizedText tokens = this._tokenizer.Tokenize(file.Content);

			IReadOnlyList<TextChunk> chunks = this._chunker.Chunk(
				file.FileId, tokens, config.ChunkSizeTokens, config.ChunkOverlapTokens);

			Disposition disposition = Classify(file, knownFiles, changedThisPass);

			if (mustFit)
			{
				// The fit needs every chunk, embedded or not, so this text has to outlive the loop.
				awaitingFit!.Add((file, chunks, disposition));

				continue;
			}

			Place(file, chunks, disposition);

			// Nothing above still references file.Content or the chunk text beyond the batch window the
			// batcher is holding, so both are collectable before the next file is pulled — which is the
			// whole point of the loop's shape, and why the window has a size rather than growing.
		}

		String? fitArtifactJson = null;

		if (awaitingFit is { Count: > 0 })
		{
			String[] corpus = awaitingFit
				.SelectMany(static entry => entry.Chunks)
				.Select(static chunk => chunk.Content)
				.ToArray();

			fitArtifactJson = ((IFittableVectorizer)vectorizer).Fit(corpus);

			foreach ((ScannedTextFile file, IReadOnlyList<TextChunk> chunks, Disposition disposition) in awaitingFit)
			{
				Place(file, chunks, disposition);
			}

			awaitingFit.Clear();
		}

		// Whatever the last window did not fill still has to be embedded, and it has to happen before the
		// single write below rather than after it.
		batcher.Flush();

		// Read after fitting: the descriptor's dimension is the rank actually reached, not the one
		// that was requested.
		ModelDescriptor descriptor = vectorizer.Descriptor;

		IndexWriteSummary summary = this._repository.Upsert(
			databasePath,
			chunksByFile,
			embeddingsByChunk,
			descriptor,
			fitArtifactJson);

		// After the vectors have committed, never before. Conditional on each file's recorded content
		// still being what was read, by the same rule a delivery marks under — nothing else is writing
		// yet, but the rule is the store's and this is not the place to hold a weaker one.
		store.MarkSynced(delivered);

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
			FilesDeferred: deferred,
			ChangesRecorded: recorded.Changes.Count);
	}

	/// <summary>What this pass does with one scanned file.</summary>
	private enum Disposition
	{
		/// <summary>Recorded at this content, and the active model has no vectors for it — or the content is new.</summary>
		Embed,

		/// <summary>Recorded at this content, and already embedded by the active model at this content.</summary>
		Unchanged,

		/// <summary>Not recorded, or recorded at other content: in flux, and the front end's.</summary>
		Deferred,
	}

	/// <summary>
	/// A file is embedded here only when the store has recorded it at exactly the content the scanner
	/// read, <em>and</em> either the comparison just recorded that content as new or the active model
	/// has no vectors for it.
	///
	/// <para>
	/// The first condition is what keeps this pass off the file's record. A file the comparison did
	/// not record was locked or appeared since; one recorded at a different hash changed since. Either
	/// way the front end has, or will have, a delivery for it, and embedding it here would mean either
	/// writing chunks under no row or marking delivered a content the row does not describe.
	/// </para>
	///
	/// <para>
	/// The second is not content equality alone, in two directions. After switching embedding
	/// implementation every file is unchanged, yet none of them has a vector in the new model's space —
	/// so skipping on the hash alone would leave the new model with a silently empty index. And a file
	/// the comparison just recorded at a new hash has vectors for the content <em>before</em> the edit,
	/// so the vector check alone would skip exactly the file that changed.
	/// </para>
	/// </summary>
	private static Disposition Classify(
		ScannedTextFile file,
		IReadOnlyDictionary<String, IndexedFileState> knownFiles,
		IReadOnlySet<String> changedThisPass)
	{
		if (!knownFiles.TryGetValue(file.FileId, out IndexedFileState? known)
			|| !String.Equals(known.FileHash, file.FileHash, StringComparison.Ordinal))
		{
			return Disposition.Deferred;
		}

		if (changedThisPass.Contains(file.FileId) || !known.HasVectorsForModel)
		{
			return Disposition.Embed;
		}

		return Disposition.Unchanged;
	}

	/// <summary>
	/// Compares the folder against the index and records the difference, through the same comparison
	/// and into the same store the front end uses — so a row written for this pass is indistinguishable
	/// from one the front end writes later, and there is exactly one derivation of a file's record.
	///
	/// <para>
	/// Built here from the same three inputs the front end's filter is built from. Nothing is running
	/// yet, so there is no hold to defer for and no logger to report survivals to: a first comparison
	/// that cannot read the folder fails the pass, which is the caller's to hear about.
	/// </para>
	/// </summary>
	private ReconcileResult Record(String analyzedFolderPath, FolderIndexStore store, IndexingConfig config)
	{
		IndexablePathFilter filter = new(
			this._metadataFolderName, this._extractors.Extensions, config.MaxTextFileSizeBytes);

		Reconciler reconciler = new(analyzedFolderPath, filter, store, new Sha256ContentHasher());

		// The pass is synchronous by design and already runs on a pool thread with no synchronization
		// context, so waiting here blocks this thread and nothing else.
		return reconciler.ReconcileAsync().GetAwaiter().GetResult();
	}

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
