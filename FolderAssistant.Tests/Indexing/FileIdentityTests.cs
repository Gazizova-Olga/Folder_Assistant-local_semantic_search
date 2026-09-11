using FluentAssertions;
using FolderAssistant.Indexing.Scanning;
using ApplicationScanner = FolderAssistant.Indexing.LocalTextFileScanner;

namespace FolderAssistant.Tests.Indexing;

/// <summary>
/// The delivered document id and the application's corpus scanner live in two assemblies and must agree on
/// every file. Asserted against the scanner itself, so a change to either derivation fails here.
/// </summary>
public sealed class FileIdentityTests
{
	[Fact]
	public async Task The_Delivered_Id_Is_The_Id_The_Application_Scanner_Derives()
	{
		using TempFolder folder = new();
		Directory.CreateDirectory(folder.Combine("docs", "deep"));
		await File.WriteAllTextAsync(folder.Combine("top.md"), "at the root");
		await File.WriteAllTextAsync(folder.Combine("docs", "deep", "note.md"), "nested");

		IReadOnlyList<(string RelativePath, string FileId)> scanned = [.. new ApplicationScanner()
			.Scan(folder.Path, maxTextFileSizeBytes: 1_000_000)
			.Select(file => (file.RelativePath, file.FileId))];

		scanned.Should().HaveCount(2);

		foreach ((string relativePath, string fileId) in scanned)
		{
			string key = IndexKey.For(folder.Path, Path.Combine(folder.Path, relativePath));

			key.Should().Be(relativePath, "both assemblies key a file the same way");
			FileIdentity.For(key).Should().Be(fileId);
		}
	}
}
