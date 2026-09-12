using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace FolderAssistant.Indexing;

/// <summary>
/// Walks the analyzed folder and returns the text files worth indexing.
///
/// <para>
/// An extension allowlist rather than a binary sniff: the set is the initial scope, and something
/// unreadable that happens to look like text costs an embedding and pollutes the index.
/// </para>
/// </summary>
internal sealed class LocalTextFileScanner
{
	private static readonly HashSet<String> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".txt", ".md", ".markdown", ".json", ".yml", ".yaml", ".xml", ".ini", ".toml", ".csv", ".log",
		".cs", ".csproj", ".props", ".targets", ".sln", ".slnx", ".js", ".ts", ".jsx", ".tsx", ".py",
		".java", ".go", ".rs", ".sql", ".ps1", ".sh", ".html", ".css", ".scss",
	};

	private static readonly HashSet<String> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
	{
		".git", ".vs", "bin", "obj", "node_modules", ".folderassistant",
	};

	/// <summary>
	/// Whether this system reads files of that extension at all.
	///
	/// <para>
	/// Exposed so that the per-file delivery path asks the same question this scanner does, rather than
	/// keeping a second list beside it. Two lists would drift, and the drift would be silent: a file
	/// indexed by one path and ignored by the other looks exactly like a file that was never saved.
	/// </para>
	/// </summary>
	public static Boolean IsIndexableExtension(String? extension)
		=> !String.IsNullOrWhiteSpace(extension) && AllowedExtensions.Contains(extension);

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
	[SuppressMessage("Minor Code Smell", "S2325:Methods and properties that do not access instance data should be static",
		Justification = "Kept an instance method by design: LocalTextFileScanner is used as an instantiable " +
			"collaborator — a new() field on the pipeline, and new LocalTextFileScanner() at test call sites. " +
			"Making it static would change how every caller reaches it for no behavioural gain.")]
	public IEnumerable<ScannedTextFile> Enumerate(String rootPath, Int64 maxTextFileSizeBytes)
	{
		if (String.IsNullOrWhiteSpace(rootPath))
		{
			throw new ArgumentException("Root path must be provided.", nameof(rootPath));
		}

		String fullRoot = Path.GetFullPath(rootPath);

		return Walk(fullRoot, fullRoot, maxTextFileSizeBytes);
	}

	/// <summary>
	/// Materialises the whole scan. The pipeline does not use this — it streams — but a caller that
	/// genuinely wants the full list gets it here rather than by writing the walk again.
	/// </summary>
	public IReadOnlyList<ScannedTextFile> Scan(String rootPath, Int64 maxTextFileSizeBytes)
		=> this.Enumerate(rootPath, maxTextFileSizeBytes).ToArray();

	private static IEnumerable<ScannedTextFile> Walk(
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

			foreach (ScannedTextFile nested in Walk(rootPath, subDirectory, maxTextFileSizeBytes))
			{
				yield return nested;
			}
		}

		foreach (String filePath in Directory.EnumerateFiles(directoryPath))
		{
			String extension = Path.GetExtension(filePath);
			if (!AllowedExtensions.Contains(extension))
			{
				continue;
			}

			FileInfo info = new(filePath);
			if (!info.Exists || info.Length > maxTextFileSizeBytes)
			{
				continue;
			}

			String content;
			try
			{
				content = File.ReadAllText(filePath, Encoding.UTF8);
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

			if (String.IsNullOrWhiteSpace(content))
			{
				continue;
			}

			String relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');

			yield return new ScannedTextFile(
				FileId: Sha256($"file::{relativePath}"),
				FullPath: filePath,
				RelativePath: relativePath,
				FileHash: Sha256(content),
				SizeBytes: info.Length,
				ModifiedUtc: info.LastWriteTimeUtc,
				Content: content,
				FileType: extension.TrimStart('.').ToLowerInvariant());
		}
	}

	private static String Sha256(String value)
		=> Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
