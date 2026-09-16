using System.Security.Cryptography;
using System.Text;
using FolderAssistant.Extraction;

namespace FolderAssistant.Indexing;

/// <summary>
/// Walks the analyzed folder and returns the text files worth indexing.
///
/// <para>
/// Which files are text is the extraction registry's answer, not a list of this scanner's own: an
/// extension allowlist rather than a binary sniff, because the set is the declared scope and something
/// unreadable that happens to look like text costs an embedding and pollutes the index. The registry
/// also decodes each file, so a file reads the same here as it does on the per-file delivery path.
/// </para>
/// </summary>
internal sealed class LocalTextFileScanner
{
	private static readonly HashSet<String> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
	{
		".git", ".vs", "bin", "obj", "node_modules", ".folderassistant",
	};

	private readonly TextExtractorRegistry _extractors;

	/// <summary>Over the registry the application runs with.</summary>
	public LocalTextFileScanner()
		: this(TextExtractorRegistry.Default)
	{
	}

	/// <param name="extractors">The one source of which extensions are read, and how each is decoded.</param>
	public LocalTextFileScanner(TextExtractorRegistry extractors)
	{
		ArgumentNullException.ThrowIfNull(extractors);
		this._extractors = extractors;
	}

	/// <summary>
	/// The directory names this scanner does not descend into, for a walker that must skip the same
	/// ones. One set, so a tool's walk and the index cannot disagree about what a folder contains.
	/// </summary>
	public static IReadOnlyCollection<String> IgnoredDirectories => IgnoredDirectoryNames;

	/// <summary>
	/// Yields indexable files one at a time, reading each one's text only as it is pulled.
	///
	/// <para>
	/// Deliberately lazy. Materialising the folder first makes every file's text resident at once,
	/// and .NET strings are UTF-16 so it costs roughly double the bytes on disk — even though a
	/// file's text is dead the moment it has been chunked. A caller that streams holds one file.
	/// </para>
	///
	/// <para>
	/// The argument check runs eagerly rather than on the first <c>MoveNext</c>. A bad root path is a
	/// programming error, and deferring it would surface it inside whatever loop happens to consume
	/// this, a long way from the call that got it wrong.
	/// </para>
	/// </summary>
	public IEnumerable<ScannedTextFile> Enumerate(String rootPath, Int64 maxTextFileSizeBytes)
	{
		if (String.IsNullOrWhiteSpace(rootPath))
		{
			throw new ArgumentException("Root path must be provided.", nameof(rootPath));
		}

		String fullRoot = Path.GetFullPath(rootPath);

		return this.Walk(fullRoot, fullRoot, maxTextFileSizeBytes);
	}

	/// <summary>
	/// Materialises the whole scan. The pipeline does not use this — it streams — but a caller that
	/// genuinely wants the full list gets it here rather than by writing the walk again.
	/// </summary>
	public IReadOnlyList<ScannedTextFile> Scan(String rootPath, Int64 maxTextFileSizeBytes)
		=> this.Enumerate(rootPath, maxTextFileSizeBytes).ToArray();

	private IEnumerable<ScannedTextFile> Walk(
		String rootPath,
		String directoryPath,
		Int64 maxTextFileSizeBytes)
	{
		foreach (String subDirectory in Directory.EnumerateDirectories(directoryPath))
		{
			if (IgnoredDirectoryNames.Contains(Path.GetFileName(subDirectory)))
			{
				continue;
			}

			foreach (ScannedTextFile nested in this.Walk(rootPath, subDirectory, maxTextFileSizeBytes))
			{
				yield return nested;
			}
		}

		foreach (String filePath in Directory.EnumerateFiles(directoryPath))
		{
			String extension = Path.GetExtension(filePath);
			ITextExtractor? extractor = this._extractors.Find(extension);
			if (extractor is null)
			{
				continue;
			}

			FileInfo info = new(filePath);
			if (!info.Exists || info.Length > maxTextFileSizeBytes)
			{
				continue;
			}

			Byte[] bytes;
			try
			{
				bytes = File.ReadAllBytes(filePath);
			}
			catch (IOException)
			{
				// Locked by another process, or gone since the enumeration listed it.
				continue;
			}
			catch (UnauthorizedAccessException)
			{
				continue;
			}

			String content = extractor.Extract(bytes);

			if (String.IsNullOrWhiteSpace(content))
			{
				continue;
			}

			String relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');

			// The content hash is taken over the bytes, before decoding. The pass embeds a file only
			// when this hash equals the one the indexing front end recorded for it (SPEC-121), so the
			// two have to agree byte for byte: decoding drops a byte-order mark and replaces sequences
			// it cannot read, so a hash of the text would disagree on exactly those files — and every
			// one of them would be deferred to the front end, quietly, on every start. The id stays a
			// hash of the path, derived as the front end derives it, because the pass writes chunks
			// under it against the row the front end recorded.
			yield return new ScannedTextFile(
				FileId: Sha256($"file::{relativePath}"),
				FullPath: filePath,
				RelativePath: relativePath,
				FileHash: Sha256(bytes),
				SizeBytes: info.Length,
				ModifiedUtc: info.LastWriteTimeUtc,
				Content: content,
				FileType: extension.TrimStart('.').ToLowerInvariant());
		}
	}

	private static String Sha256(String value)
		=> Sha256(Encoding.UTF8.GetBytes(value));

	private static String Sha256(Byte[] bytes)
		=> Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
