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

	/// <summary>Scans <paramref name="rootPath"/> for indexable text files.</summary>
	public IReadOnlyList<ScannedTextFile> Scan(String rootPath, Int64 maxTextFileSizeBytes)
	{
		if (String.IsNullOrWhiteSpace(rootPath))
		{
			throw new ArgumentException("Root path must be provided.", nameof(rootPath));
		}

		String fullRoot = Path.GetFullPath(rootPath);
		List<ScannedTextFile> found = [];

		Enumerate(fullRoot, fullRoot, maxTextFileSizeBytes, found);

		return found;
	}

	private static void Enumerate(
		String rootPath,
		String directoryPath,
		Int64 maxTextFileSizeBytes,
		List<ScannedTextFile> output)
	{
		foreach (String subDirectory in Directory.EnumerateDirectories(directoryPath))
		{
			if (IgnoredDirectoryNames.Contains(Path.GetFileName(subDirectory)))
			{
				continue;
			}

			Enumerate(rootPath, subDirectory, maxTextFileSizeBytes, output);
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

			output.Add(new ScannedTextFile(
				FileId: Sha256($"file::{relativePath}"),
				FullPath: filePath,
				RelativePath: relativePath,
				FileHash: Sha256(content),
				SizeBytes: info.Length,
				ModifiedUtc: info.LastWriteTimeUtc,
				Content: content,
				FileType: extension.TrimStart('.').ToLowerInvariant()));
		}
	}

	private static String Sha256(String value)
		=> Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
