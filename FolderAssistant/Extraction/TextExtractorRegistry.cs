namespace FolderAssistant.Extraction;

/// <summary>
/// The one source of which files this system reads: every extension an extractor claims, and which
/// extractor claims it. The scanner, the per-file delivery, the watcher's filter and the text search
/// all ask this instance rather than keeping a list of their own, because two lists drift and the
/// drift is silent — a file indexed by one path and ignored by another looks exactly like a file that
/// was never saved.
///
/// <para>
/// An extension claimed by two extractors is refused at construction. Letting the later one win would
/// make which text a file yields depend on registration order, which nothing reading the index could
/// tell from the outside.
/// </para>
/// </summary>
internal sealed class TextExtractorRegistry
{
	/// <summary>The registry the application runs with: plain text, and nothing else yet.</summary>
	public static TextExtractorRegistry Default { get; } = new(new PlainTextExtractor());

	private readonly Dictionary<String, ITextExtractor> _byExtension = new(StringComparer.OrdinalIgnoreCase);

	public TextExtractorRegistry(params ITextExtractor[] extractors)
	{
		ArgumentNullException.ThrowIfNull(extractors);

		foreach (ITextExtractor extractor in extractors)
		{
			ArgumentNullException.ThrowIfNull(extractor);

			foreach (String extension in extractor.Extensions)
			{
				if (String.IsNullOrWhiteSpace(extension) || extension[0] != '.')
				{
					throw new ArgumentException($"Extension '{extension}' must start with a dot.", nameof(extractors));
				}

				if (!this._byExtension.TryAdd(extension, extractor))
				{
					throw new ArgumentException(
						$"Extension '{extension}' is claimed by both {this._byExtension[extension].GetType().Name} and {extractor.GetType().Name}.",
						nameof(extractors));
				}
			}
		}
	}

	/// <summary>Every extension some extractor reads, for a walker that takes a list rather than asking per file.</summary>
	public IReadOnlyCollection<String> Extensions => this._byExtension.Keys;

	/// <summary>Whether this system reads files of that extension at all.</summary>
	public Boolean IsSupported(String? extension)
		=> !String.IsNullOrWhiteSpace(extension) && this._byExtension.ContainsKey(extension);

	/// <summary>
	/// Whether a file of that extension can be scanned line by line as it lies on disk. False for an
	/// extension nothing reads, so a caller can ask this alone.
	/// </summary>
	public Boolean SupportsRawLineScanning(String? extension)
		=> this.Find(extension)?.SupportsRawLineScanning == true;

	/// <summary>The extractor for that extension, or null when nothing reads it.</summary>
	public ITextExtractor? Find(String? extension)
		=> String.IsNullOrWhiteSpace(extension) ? null : this._byExtension.GetValueOrDefault(extension);

	/// <summary>The extractor for that extension; throws when nothing reads it, because the caller should have asked first.</summary>
	public ITextExtractor Require(String? extension)
		=> this.Find(extension) ?? throw new NotSupportedException($"No extractor reads '{extension}'.");
}
