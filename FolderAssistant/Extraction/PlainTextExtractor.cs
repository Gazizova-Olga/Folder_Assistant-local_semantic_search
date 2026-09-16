using System.Text;

namespace FolderAssistant.Extraction;

/// <summary>
/// The formats whose bytes are the text: source, markup, configuration and notes. Decoded as
/// <c>File.ReadAllText</c> decodes — UTF-8 unless a byte-order mark says otherwise, with the mark left
/// out of the text — so a file reads the same here as it does through the tools.
///
/// <para>
/// The list is the declared scope of the index, not a guess at what might be text: something
/// unreadable that happens to look like text costs an embedding and pollutes the index (SPEC-120).
/// </para>
/// </summary>
internal sealed class PlainTextExtractor : ITextExtractor
{
	private static readonly HashSet<String> KnownExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".txt", ".md", ".markdown", ".json", ".yml", ".yaml", ".xml", ".ini", ".toml", ".csv", ".log",
		".cs", ".csproj", ".props", ".targets", ".sln", ".slnx", ".js", ".ts", ".jsx", ".tsx", ".py",
		".java", ".go", ".rs", ".sql", ".ps1", ".sh", ".html", ".css", ".scss",
	};

	public IReadOnlyCollection<String> Extensions => KnownExtensions;

	public Boolean SupportsRawLineScanning => true;

	public String Extract(Byte[] bytes)
	{
		ArgumentNullException.ThrowIfNull(bytes);

		using StreamReader reader = new(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

		return reader.ReadToEnd();
	}
}
