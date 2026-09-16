using System.Text;

namespace FolderAssistant.Tools;

/// <summary>
/// What every tool that opens a file by a resolved path agrees on: the size above which a file is not
/// read at all, the sniff that tells a text file from a binary one, and a share-read open that does not
/// retry. One place, so the read holder and the mutation holder cannot mean different things by
/// "a text file".
/// </summary>
internal static class TextFile
{
	internal const Int64 MaxBytes = 16L * 1024 * 1024;
	internal const Int32 SniffBytes = 8 * 1024;

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
}
