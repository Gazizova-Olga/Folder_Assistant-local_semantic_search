using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FolderAssistant.Tools;

/// <summary>
/// The two questions only the Windows volume can answer for the containment guard: what a drive letter
/// stands for, and what other names a file has. Both are best-effort by design — a shape this code does
/// not recognise, or a volume that will not enumerate, is reported as a note, and the textual rule in
/// <see cref="WorkspacePathGuard"/> decides alone.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsPathProbe
{
	private const Int32 ErrorFileNotFound = 2;
	private const Int32 ErrorPathNotFound = 3;
	private const Int32 ErrorMoreData = 234;
	private static readonly IntPtr InvalidHandle = new(-1);

	/// <summary>
	/// Replaces a <c>subst</c> drive letter in <paramref name="fullPath"/> with the path it stands for.
	/// A real volume (<c>\Device\…</c>) is returned as written. A substitution onto another substituted
	/// drive, or a target of an unrecognised shape, is returned as written with a note: one level is
	/// resolved, and a chain is left to the textual rule rather than followed on trust.
	/// </summary>
	public static (String Path, String? Note) ResolveSubstitution(String fullPath)
	{
		String? drive = DriveOf(fullPath);
		if (drive is null)
		{
			return (fullPath, null);
		}

		(DosDeviceKind kind, String target) = QueryDosDevice(drive);
		switch (kind)
		{
			case DosDeviceKind.Substitution:
				String? innerDrive = DriveOf(target);
				if (innerDrive is not null && QueryDosDevice(innerDrive).Kind == DosDeviceKind.Substitution)
				{
					return (fullPath, $"physical identity not verified: '{drive}' is a substitution onto another substituted drive ('{target}')");
				}

				String rest = fullPath[2..].TrimStart(System.IO.Path.DirectorySeparatorChar);
				String resolved = System.IO.Path.TrimEndingDirectorySeparator(
					System.IO.Path.GetFullPath(System.IO.Path.Join(target, rest)));

				return (resolved, null);

			case DosDeviceKind.Unrecognised:
				return (fullPath, $"physical identity not verified: '{drive}' maps to '{target}', a target this code does not recognise");

			default:
				return (fullPath, null);
		}
	}

	/// <summary>
	/// Every name the volume has for the file at <paramref name="physicalPath"/>, as full paths on that
	/// file's volume root. A volume that cannot enumerate returns no names and a note; a file that is not
	/// there returns no names and no note, because the caller already knows that.
	/// </summary>
	public static (IReadOnlyList<String> Names, String? Note) HardLinkNames(String physicalPath)
	{
		String? volumeRoot = System.IO.Path.GetPathRoot(physicalPath);
		if (volumeRoot is null)
		{
			return ([], null);
		}

		// Names come back relative to the volume, with a leading separator; the volume root ends in one.
		String volume = volumeRoot.TrimEnd(System.IO.Path.DirectorySeparatorChar);
		List<String> names = [];
		Char[] buffer = new Char[Int16.MaxValue];
		UInt32 length = (UInt32)buffer.Length;

		IntPtr handle = FindFirstFileNameW(physicalPath, 0, ref length, buffer);
		if (handle == InvalidHandle)
		{
			Int32 error = Marshal.GetLastWin32Error();
			if (error is ErrorFileNotFound or ErrorPathNotFound)
			{
				return ([], null);
			}

			return ([], $"hard-link names not enumerated: the volume returned error {error}");
		}

		try
		{
			names.Add(volume + new String(buffer, 0, (Int32)length).TrimEnd('\0'));

			length = (UInt32)buffer.Length;
			while (FindNextFileNameW(handle, ref length, buffer))
			{
				names.Add(volume + new String(buffer, 0, (Int32)length).TrimEnd('\0'));
				length = (UInt32)buffer.Length;
			}

			Int32 stop = Marshal.GetLastWin32Error();

			return stop == ErrorMoreData
				? ([], "hard-link names not enumerated: a name was longer than the buffer")
				: (names, null);
		}
		finally
		{
			FindClose(handle);
		}
	}

	/// <summary>The <c>X:</c> of a drive-letter path, or null for a UNC or rootless one.</summary>
	private static String? DriveOf(String path)
		=> path.Length >= 2 && path[1] == ':' && Char.IsAsciiLetter(path[0])
			? path[..2]
			: null;

	private static (DosDeviceKind Kind, String Target) QueryDosDevice(String drive)
	{
		Char[] buffer = new Char[4096];
		UInt32 stored = QueryDosDeviceW(drive, buffer, (UInt32)buffer.Length);
		if (stored == 0)
		{
			return (DosDeviceKind.Missing, String.Empty);
		}

		// The buffer is a multi-string; the first entry is the current mapping.
		Int32 end = Array.IndexOf(buffer, '\0');
		String target = new(buffer, 0, end < 0 ? buffer.Length : end);

		if (target.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
		{
			return (DosDeviceKind.Device, target);
		}

		if (target.StartsWith(@"\??\", StringComparison.Ordinal)
			&& target.Length >= 7
			&& DriveOf(target[4..]) is not null
			&& target[6] == System.IO.Path.DirectorySeparatorChar)
		{
			return (DosDeviceKind.Substitution, target[4..]);
		}

		return (DosDeviceKind.Unrecognised, target);
	}

	private enum DosDeviceKind
	{
		Missing,
		Device,
		Substitution,
		Unrecognised,
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern UInt32 QueryDosDeviceW(String lpDeviceName, [Out] Char[] lpTargetPath, UInt32 ucchMax);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr FindFirstFileNameW(String lpFileName, UInt32 dwFlags, ref UInt32 stringLength, [Out] Char[] linkName);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern Boolean FindNextFileNameW(IntPtr hFindStream, ref UInt32 stringLength, [Out] Char[] linkName);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern Boolean FindClose(IntPtr hFindFile);
}
