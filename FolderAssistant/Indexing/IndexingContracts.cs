// The types the pipeline stages hand to each other. Each was declared beside the stage that
// produces it, but every one of them is consumed by a later stage and by the repository, so no
// single stage owns its shape.

namespace FolderAssistant.Indexing;

/// <summary>One text file found under the analyzed folder, with its content already read.</summary>
internal sealed record ScannedTextFile(
	String FileId,
	String FullPath,
	String RelativePath,
	String FileHash,
	Int64 SizeBytes,
	DateTime ModifiedUtc,
	String Content,
	String FileType);

/// <summary>One overlapping window of tokens, and the text it covers.</summary>
internal sealed record TextChunk(
	String ChunkId,
	Int32 Index,
	Int32 TokenStart,
	Int32 TokenEnd,
	String ChunkHash,
	String Content);

