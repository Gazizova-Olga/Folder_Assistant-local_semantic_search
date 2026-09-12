using System.Text;
using FluentAssertions;
using FolderAssistant.Indexing.Scanning;
using ApplicationScanner = FolderAssistant.Indexing.LocalTextFileScanner;

namespace FolderAssistant.Tests.Indexing;

/// <summary>
/// The content hash the front end records and the one the application's corpus scanner records land in
/// the same column of the same record, from two assemblies. They must agree on every file: the first
/// comparison after a whole-folder pass would otherwise find the entire corpus modified and deliver it
/// again — correctly, silently, at full cost. Asserted against the scanner itself, so a change to either
/// side fails here.
/// </summary>
public sealed class ContentHashAgreementTests
{
	[Fact]
	public async Task The_Front_End_Hashes_A_File_Exactly_As_The_Application_Scanner_Does()
	{
		using TempFolder folder = new();

		// A byte-order mark and a multi-byte character are what separate a hash of the decoded text from
		// a hash of the bytes, so agreement on this file is agreement on the case that could differ.
		byte[] marked = [.. Encoding.UTF8.Preamble, .. "naïve text behind a mark"u8];
		await File.WriteAllBytesAsync(folder.Combine("marked.md"), marked);
		await File.WriteAllTextAsync(folder.Combine("plain.md"), "plain text, no mark");

		Dictionary<string, string> scanned = new ApplicationScanner()
			.Scan(folder.Path, maxTextFileSizeBytes: 1_000_000)
			.ToDictionary(file => file.RelativePath, file => file.FileHash, StringComparer.Ordinal);

		scanned.Should().HaveCount(2);

		Sha256ContentHasher hasher = new();

		foreach ((string relativePath, string fileHash) in scanned)
		{
			string ours = await hasher.HashAsync(folder.Combine(relativePath));

			ours.Should().Be(fileHash, "both writers of the record hash the bytes of {0}", relativePath);
		}
	}
}
