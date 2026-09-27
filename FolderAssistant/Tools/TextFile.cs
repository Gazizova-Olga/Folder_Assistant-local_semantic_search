using System.Security.Cryptography;
using System.Text;

namespace FolderAssistant.Tools;

/// <summary>
/// What every tool that opens a file by a resolved path agrees on: the size above which a file is not
/// read at all, the sniff that tells a text file from a binary one, a share-read open that does not
/// retry, and what a file's version is. One place, so the read holder and the mutation holder cannot
/// mean different things by "a text file" or by "the same file I was shown".
/// </summary>
internal static class TextFile
{
	internal const Int64 MaxBytes = 16L * 1024 * 1024;
	internal const Int32 SniffBytes = 8 * 1024;

	/// <summary>
	/// How much of the digest a version carries. Sixteen hex characters is 64 bits, which is far past
	/// what an accidental collision needs — this tells an edited file from an unedited one, it is not a
	/// defence against someone constructing a match — and it is short enough for a model to copy from a
	/// read into an edit without transcribing it wrongly, which a full digest is not.
	/// </summary>
	internal const Int32 VersionLength = 16;

	/// <summary>
	/// The version of the bytes: the content a caller was shown, in a form it can hand back. SHA-256
	/// over the <em>bytes</em>, as the indexer hashes a file, so a byte-order mark is part of the
	/// content here as it is there rather than something one reader drops and another keeps.
	/// </summary>
	public static String Version(ReadOnlySpan<Byte> bytes)
		=> Convert.ToHexString(SHA256.HashData(bytes))[..VersionLength].ToLowerInvariant();

	/// <summary>
	/// The version of a file as it is on disk right now. Read separately from any decoding, because the
	/// version is of the file and not of what a decoder made of it.
	/// </summary>
	public static String VersionOf(String fullPath)
		=> Version(File.ReadAllBytes(fullPath));

	/// <summary>
	/// The file behind a resolved path, or the exception that says why it cannot be read: not there, a
	/// directory, above the size bound, or — by its first bytes — not text.
	/// </summary>
	/// <param name="shown">The path as a result or a message shows it.</param>
	public static FileInfo Existing(GuardedPath resolved, String shown)
	{
		FileInfo file = new(resolved.FullPath);
		if (!file.Exists)
		{
			if (Directory.Exists(resolved.FullPath))
			{
				throw new FileNotFoundException($"'{shown}' is a directory, not a file.", resolved.FullPath);
			}

			throw new FileNotFoundException($"'{shown}' is not a file in the workspace.", resolved.FullPath);
		}

		if (file.Length > MaxBytes)
		{
			throw new IOException($"'{shown}' is {file.Length:N0} bytes, above the {MaxBytes:N0}-byte bound.");
		}

		Byte[] sniff = new Byte[SniffBytes];
		Int32 read;
		using (FileStream stream = new(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
		{
			read = stream.Read(sniff, 0, sniff.Length);
		}

		if (Array.IndexOf(sniff, (Byte)0, 0, read) >= 0)
		{
			throw new InvalidDataException($"'{shown}' is not a text file.");
		}

		return file;
	}

	/// <summary>A share-read open that decodes UTF-8 and consumes a byte-order mark. It does not retry.</summary>
	public static StreamReader OpenText(FileInfo file)
		=> new(
			new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read),
			Encoding.UTF8,
			detectEncodingFromByteOrderMarks: true);

	/// <summary>
	/// The same decoding, over bytes already read. A caller that hands out a <see cref="Version"/> beside
	/// the text has to decode <em>those</em> bytes: hashing one read and decoding another would certify
	/// content nobody was shown, which is worse than handing out no version at all.
	/// </summary>
	public static StreamReader OpenText(Byte[] bytes)
		=> new(
			new MemoryStream(bytes, writable: false),
			Encoding.UTF8,
			detectEncodingFromByteOrderMarks: true);
}
