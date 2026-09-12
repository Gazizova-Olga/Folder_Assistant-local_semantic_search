using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using FolderAssistant.Indexing.Scanning;

namespace FolderAssistant.Tests.Indexing;

/// <summary>
/// Change detection hashes a file's bytes, and nothing else — because another writer records a hash
/// of the same file in the same column, and the two agree only if they hash the same thing.
/// </summary>
public sealed class Sha256ContentHasherTests
{
	[Fact]
	public async Task The_Hash_Is_Sha256_Of_The_Bytes_In_Lowercase_Hex()
	{
		using TempFolder folder = new();
		string path = folder.Combine("abc.txt");
		await File.WriteAllTextAsync(path, "abc");

		string hash = await new Sha256ContentHasher().HashAsync(path);

		// The published test vector for "abc".
		hash.Should().Be("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
	}

	/// <summary>
	/// The reason it is the bytes. A decoder drops the byte-order mark, so a hash of the decoded text
	/// would be the hash of a different sequence from the one on disk — and the writer this has to
	/// agree with hashes what is on disk.
	/// </summary>
	[Fact]
	public async Task A_Byte_Order_Mark_Is_Part_Of_What_Is_Hashed()
	{
		using TempFolder folder = new();
		string path = folder.Combine("bom.txt");
		byte[] bytes = [.. Encoding.UTF8.Preamble, .. "abc"u8];
		await File.WriteAllBytesAsync(path, bytes);

		string hash = await new Sha256ContentHasher().HashAsync(path);

		hash.Should().Be(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
		hash.Should().NotBe(Convert.ToHexString(SHA256.HashData("abc"u8)).ToLowerInvariant(),
			"the mark is on disk, and the hash is of what is on disk");
	}

	/// <summary>
	/// A file held by a writer is reported as unreadable rather than hashed torn. The callers treat
	/// that as ordinary and try again; what they must never get is a hash of half a file.
	/// </summary>
	[Fact]
	public async Task A_File_Held_By_A_Writer_Is_Refused_Not_Hashed()
	{
		using TempFolder folder = new();
		string path = folder.Combine("held.txt");
		await File.WriteAllTextAsync(path, "in progress");

		using FileStream writer = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

		Func<Task> hash = () => new Sha256ContentHasher().HashAsync(path);

		await hash.Should().ThrowAsync<IOException>();
	}
}
