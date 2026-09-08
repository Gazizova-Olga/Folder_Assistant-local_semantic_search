using FolderAssistant.Embedding;
using FolderAssistant.Persistence;

namespace FolderAssistant.Indexing;

/// <summary>What one indexing pass got through.</summary>
internal sealed record IndexingResult(
	Int32 FilesScanned,
	Int32 FilesIndexed,
	Int32 ChunksIndexed,
	Int32 VectorsIndexed);

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

	public FolderIndexingPipeline()
		: this(null)
	{
	}

	/// <summary>Composition-time override; with none, the programmable baseline is built from config.</summary>
	internal FolderIndexingPipeline(IVectorizer? vectorizer)
	{
		this._vectorizer = vectorizer;
	}

	public IndexingResult Run(String analyzedFolderPath, String databasePath, IndexingConfig config)
	{
		ArgumentNullException.ThrowIfNull(config);

		IVectorizer vectorizer = this._vectorizer
			?? new ProgrammableEmbeddingVectorizer(config.ModelVersionId, config.VectorDimension);

		IReadOnlyList<ScannedTextFile> files = this._scanner.Scan(analyzedFolderPath, config.MaxTextFileSizeBytes);

		Dictionary<String, IReadOnlyList<TextChunk>> chunksByFile = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<String, EmbeddingResult> embeddingsByChunk = new(StringComparer.OrdinalIgnoreCase);

		foreach (ScannedTextFile file in files)
		{
			TokenizedText tokens = this._tokenizer.Tokenize(file.Content);

			IReadOnlyList<TextChunk> chunks = this._chunker.Chunk(
				file.FileId, tokens.Tokens, config.ChunkSizeTokens, config.ChunkOverlapTokens);

			chunksByFile[file.FileId] = chunks;

			if (chunks.Count == 0)
			{
				continue;
			}

			IReadOnlyList<EmbeddingResult> vectors = vectorizer.Vectorize(
				chunks.Select(static chunk => chunk.Content).ToArray(),
				EmbeddingKind.Document);

			for (Int32 i = 0; i < chunks.Count; i++)
			{
				embeddingsByChunk[chunks[i].ChunkId] = vectors[i];
			}
		}

		IndexWriteSummary summary = this._repository.Upsert(
			databasePath,
			files,
			chunksByFile,
			embeddingsByChunk,
			vectorizer.Descriptor);

		return new IndexingResult(
			FilesScanned: files.Count,
			FilesIndexed: summary.FilesUpserted,
			ChunksIndexed: summary.ChunksUpserted,
			VectorsIndexed: summary.VectorsUpserted);
	}
}
