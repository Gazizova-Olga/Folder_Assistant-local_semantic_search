using System.Security.Cryptography;
using System.Text;
using FolderAssistant.Embedding;
using FolderAssistant.Indexing.Outbox;
using FolderAssistant.Persistence;

namespace FolderAssistant.Indexing;

/// <summary>
/// Turns one delivered file into embedded, searchable content: the application's half of the seam the
/// indexing library calls back through.
///
/// <para>
/// The library knows nothing of chunking, embedding or vectors, and this is the whole of what crosses
/// back. It is the per-file counterpart of <see cref="FolderIndexingPipeline"/>, which does the same
/// work for a whole folder at once, and the two deliberately share the pieces that must not diverge —
/// the same tokenizer, the same chunker, the same embed window. Two definitions of "how a file becomes
/// chunks" would eventually disagree, and the symptom would be a file that retrieves differently
/// depending on which path indexed it.
/// </para>
///
/// <para>
/// <strong>The delivered id is the identity.</strong> The library derives it from the file's path and
/// hands it over, so chunks are keyed on it directly rather than on anything derived here. A file that
/// is moved and then edited stays one file, under one id, instead of becoming two.
/// </para>
///
/// <para>
/// <strong>Deliveries are serialised, and the claim for that is narrower than it looks.</strong> The
/// dispatcher can deliver several files at once and SQLite takes one writer, so the gate makes them
/// queue in process instead of contending for the write lock. It does <em>not</em> prevent a failure:
/// removing it leaves every test here passing, because the busy timeout every connection sets absorbs
/// the contention at this scale. What it buys is that waiting is bounded by the queue rather than by a
/// timeout and its retries. Kept on that basis, and recorded as undemonstrated rather than described as
/// though a test proved it.
/// </para>
/// </summary>
internal sealed class RagBridgeVectorizationService : IVectorizationService, IDisposable
{
	private readonly String _analyzedFolderPath;
	private readonly String _databasePath;
	private readonly IVectorizer _vectorizer;
	private readonly FolderIndexRepository _repository;
	private readonly IVectorStoreReader _vectorStoreReader;
	private readonly IndexingConfig _config;
	private readonly String _metadataFolderName;

	private readonly SimpleTokenizer _tokenizer = new();
	private readonly TextChunker _chunker = new();

	// One writer at a time. Undemonstrated by the suite — see the class remarks for what that means.
	private readonly SemaphoreSlim _gate = new(1, 1);

	private Boolean _fitLoaded;

	public RagBridgeVectorizationService(
		String analyzedFolderPath,
		String databasePath,
		IVectorizer vectorizer,
		FolderIndexRepository repository,
		IVectorStoreReader vectorStoreReader,
		IndexingConfig config,
		String metadataFolderName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(analyzedFolderPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
		ArgumentNullException.ThrowIfNull(vectorizer);
		ArgumentNullException.ThrowIfNull(repository);
		ArgumentNullException.ThrowIfNull(vectorStoreReader);
		ArgumentNullException.ThrowIfNull(config);
		ArgumentException.ThrowIfNullOrWhiteSpace(metadataFolderName);

		this._analyzedFolderPath = Path.GetFullPath(analyzedFolderPath);
		this._databasePath = databasePath;
		this._vectorizer = vectorizer;
		this._repository = repository;
		this._vectorStoreReader = vectorStoreReader;
		this._config = config;
		this._metadataFolderName = metadataFolderName;
	}

	/// <summary>
	/// Embeds the delivered file, replacing whatever was embedded under this id before.
	///
	/// <para>
	/// Several conditions end in writing nothing, and they are not failures: the database lives inside
	/// the folder being watched, so a write here would otherwise be a change there, and each index
	/// would trigger the next one forever. A file whose extension this system does not read is
	/// likewise not an error — it is simply not text.
	/// </para>
	/// </summary>
	public async Task UpsertAsync(String docId, Stream content, FileMetadata metadata, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(docId);
		ArgumentNullException.ThrowIfNull(content);
		ArgumentNullException.ThrowIfNull(metadata);

		if (this.IsInsideMetadataFolder(metadata.AbsolutePath) || !LocalTextFileScanner.IsIndexableExtension(metadata.Extension))
		{
			return;
		}

		String text;

		using (StreamReader reader = new(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
		{
			text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
		}

		await this._gate.WaitAsync(cancellationToken).ConfigureAwait(false);

		try
		{
			// A corpus-fitted embedder cannot be fitted from one file, and embedding against no fit at all
			// would store vectors in a space nothing can query. The cold pass owns fitting; until it has
			// run, this path has nothing it can honestly write.
			if (!this.EnsureFitLoaded())
			{
				return;
			}

			TokenizedText tokens = this._tokenizer.Tokenize(text);

			IReadOnlyList<TextChunk> chunks = this._chunker.Chunk(
				docId, tokens, this._config.ChunkSizeTokens, this._config.ChunkOverlapTokens);

			Dictionary<String, EmbeddingResult> embeddings = new(StringComparer.OrdinalIgnoreCase);

			// The same bound the corpus pass uses. A single file can be large enough to matter on its own.
			ChunkBatcher batcher = new(this._vectorizer, embeddings, this._config.EmbeddingBatchSizeChunks);
			batcher.Add(chunks);
			batcher.Flush();

			// Chunks and vectors under the delivered id, and nothing about the file's record. The record
			// is the store's: a delivery that wrote it back here, with the hash it was handed before a
			// slow embed, reverted whatever the front end had recorded meanwhile — and the delivery
			// queued for that newer content then found its work already done (SPEC-121).
			this._repository.UpsertSingleFile(
				this._databasePath,
				docId,
				[.. chunks.Select(static chunk => chunk.ToMetadata())],
				embeddings,
				this._vectorizer.Descriptor);
		}
		finally
		{
			this._gate.Release();
		}
	}

	/// <summary>Removes everything embedded under the id. A file that was never indexed is not an error.</summary>
	public async Task DeleteAsync(String docId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(docId);

		await this._gate.WaitAsync(cancellationToken).ConfigureAwait(false);

		try
		{
			this._repository.DeleteFile(this._databasePath, docId);
		}
		finally
		{
			this._gate.Release();
		}
	}

	/// <summary>
	/// Restores the stored fit for a corpus-fitted embedder, once per instance, and reports whether this
	/// path can embed at all.
	///
	/// <para>
	/// An embedder that needs no fit always can. One that does can only once a corpus pass has stored an
	/// artifact, because the fit has to be the same on the write and the query side — a vector embedded
	/// against a different fit is not wrong in any way a query can detect, it simply ranks as though it
	/// meant something else.
	/// </para>
	/// </summary>
	private Boolean EnsureFitLoaded()
	{
		if (this._vectorizer is not IFittableVectorizer fittable)
		{
			return true;
		}

		if (this._fitLoaded)
		{
			return true;
		}

		String? artifact = this._vectorStoreReader.ReadFitArtifact(
			this._databasePath, this._vectorizer.Descriptor.ModelVersionId);

		if (artifact is null)
		{
			return false;
		}

		fittable.LoadFit(artifact);
		this._fitLoaded = true;

		return true;
	}

	/// <summary>
	/// True when the path is inside the metadata folder. The database is in there, so indexing it would
	/// make every write a change to the watched folder and the indexer would never go quiet.
	/// </summary>
	private Boolean IsInsideMetadataFolder(String absolutePath)
	{
		String relative = Path.GetRelativePath(this._analyzedFolderPath, absolutePath);

		// Split on an explicit array, not two loose characters: the two-char call binds to
		// Split(char, int, StringSplitOptions) instead, reading the second separator as a count. That
		// exact mis-binding already shipped once in this system's other metadata-folder guard, in a
		// green build, and was found by an analyzer rather than by a test.
		return relative
			.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
			.Any(segment => String.Equals(segment, this._metadataFolderName, StringComparison.OrdinalIgnoreCase));
	}

	public void Dispose() => this._gate.Dispose();
}
