namespace FolderAssistant.Extraction;

/// <summary>
/// Turns the bytes of a file in one family of formats into the text the indexer chunks and the tools
/// read. One extractor claims a set of extensions and says whether its formats can be scanned as raw
/// lines — whether the bytes on disk <em>are</em> the text — which is what separates a Markdown file
/// from a document whose text has to be pulled out of a container.
///
/// <para>
/// Bytes in rather than a path or a stream: the scanner and the delivery both hash a file's bytes
/// before decoding them (SPEC-120), so the bytes are already in hand, and an extractor that took a
/// path would open the file a second time under a writer the first open just survived.
/// </para>
/// </summary>
internal interface ITextExtractor
{
	/// <summary>The extensions this extractor reads, each with its leading dot, compared without case.</summary>
	IReadOnlyCollection<String> Extensions { get; }

	/// <summary>
	/// Whether a file of these formats can be read line by line as it lies on disk. True for plain
	/// text; false for any format whose text sits inside a container, which the text search must not
	/// scan raw and which the file readers must not hand back as bytes.
	/// </summary>
	Boolean SupportsRawLineScanning { get; }

	/// <summary>The text of a file, from its bytes. Throws when the bytes are not a document of this format.</summary>
	String Extract(Byte[] bytes);
}
