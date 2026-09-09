// The types the pipeline stages hand to each other. Each was declared beside the stage that
// produces it, but every one of them is consumed by a later stage and by the repository, so no
// single stage owns its shape.
//
// They come in pairs: one carrying text, one without. The text-carrying half is streamed and
// dropped; only the other half may be accumulated. Nothing that carries text is ever persisted.

namespace FolderAssistant.Indexing;

/// <summary>
/// One text file found under the analyzed folder, with its content already read. Streamed through
/// the pipeline and dropped once chunked — never held across files.
/// </summary>
internal sealed record ScannedTextFile(
	String FileId,
	String FullPath,
	String RelativePath,
	String FileHash,
	Int64 SizeBytes,
	DateTime ModifiedUtc,
	String Content,
	String FileType)
{
	public ScannedFile ToMetadata()
		=> new(this.FileId, this.RelativePath, this.FileHash, this.SizeBytes, this.ModifiedUtc, this.FileType);
}

/// <summary>
/// A scanned file without its text: everything the write path stores, and nothing it does not.
///
/// <para>
/// The pair exists so that the pipeline can stream. A file's text is read to be chunked and is dead
/// immediately afterwards, so the accumulating half of the pass must not be able to reference it —
/// and the way to guarantee that is a type that has no field to put it in.
/// </para>
/// </summary>
internal sealed record ScannedFile(
	String FileId,
	String RelativePath,
	String FileHash,
	Int64 SizeBytes,
	DateTime ModifiedUtc,
	String FileType);

/// <summary>One overlapping window of tokens, and the text it covers. Lives long enough to be embedded.</summary>
internal sealed record TextChunk(
	String ChunkId,
	Int32 Index,
	Int32 TokenStart,
	Int32 TokenEnd,
	String ChunkHash,
	String Content)
{
	public ChunkMetadata ToMetadata()
		=> new(this.ChunkId, this.Index, this.TokenStart, this.TokenEnd, this.ChunkHash);
}

/// <summary>A chunk without its text — which is all `chunk_manifest` has ever held.</summary>
internal sealed record ChunkMetadata(
	String ChunkId,
	Int32 Index,
	Int32 TokenStart,
	Int32 TokenEnd,
	String ChunkHash);
