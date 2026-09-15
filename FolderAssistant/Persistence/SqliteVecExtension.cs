using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace FolderAssistant.Persistence;

/// <summary>
/// Loads the native <c>sqlite-vec</c> extension onto a connection.
///
/// <para>
/// The extension is a per-RID native library rather than managed code, and it sits on no loader search
/// path: a portable build leaves it under <c>runtimes/&lt;rid&gt;/native/</c>, and only a RID-specific
/// publish flattens it beside the assembly. The path is therefore resolved explicitly. Passing a bare
/// name instead fails with "the specified module could not be found", which reads like a missing
/// package rather than a missing path and sends the reader looking in the wrong place.
/// </para>
///
/// <para>
/// The package ships binaries for win-x64, linux-x64, linux-arm64, osx-x64 and osx-arm64 — there is no
/// win-arm64 and no musl build. So this backend does not exist everywhere the managed code runs, and
/// that is a property of the backend rather than an accident of packaging: it belongs in the
/// comparison, not in a footnote (<c>SPEC-131</c>).
/// </para>
/// </summary>
internal static class SqliteVecExtension
{
	/// <summary>True when a native binary for the current platform is present and can be loaded.</summary>
	public static Boolean IsAvailable => ResolvePath() is not null;

	/// <summary>
	/// True when the package ships a binary for this platform at all — so a test can tell "the binary is
	/// missing here, as expected" from "the binary should be here and is not", and fail on the second.
	/// </summary>
	public static Boolean PlatformHasBinary => CurrentPlatform().Rid is not null;

	public static void LoadOnto(SqliteConnection connection)
	{
		String path = ResolvePath()
			?? throw new PlatformNotSupportedException(
				"The sqlite-vec native extension is not available on this platform. It ships binaries for "
				+ "win-x64, linux-x64, linux-arm64, osx-x64 and osx-arm64 only.");

		connection.EnableExtensions(true);
		connection.LoadExtension(path);
	}

	private static String? ResolvePath()
	{
		(String? rid, String? fileName) = CurrentPlatform();

		if (rid is null || fileName is null)
		{
			return null;
		}

		// A RID-specific publish flattens natives beside the assembly; a portable build nests them.
		String flat = Path.Combine(AppContext.BaseDirectory, fileName);
		if (File.Exists(flat))
		{
			return flat;
		}

		String nested = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", fileName);

		return File.Exists(nested) ? nested : null;
	}

	private static (String? Rid, String? FileName) CurrentPlatform()
	{
		Architecture architecture = RuntimeInformation.ProcessArchitecture;

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			return architecture == Architecture.X64 ? ("win-x64", "vec0.dll") : (null, null);
		}

		if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
		{
			return architecture switch
			{
				Architecture.Arm64 => ("osx-arm64", "vec0.dylib"),
				Architecture.X64 => ("osx-x64", "vec0.dylib"),
				_ => (null, null),
			};
		}

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			// The linux-* binaries are built against glibc. On a musl system the file is present in a
			// portable build and fails to load, so the platform is reported as uncovered up front.
			if (RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.OrdinalIgnoreCase))
			{
				return (null, null);
			}

			return architecture switch
			{
				Architecture.Arm64 => ("linux-arm64", "vec0.so"),
				Architecture.X64 => ("linux-x64", "vec0.so"),
				_ => (null, null),
			};
		}

		return (null, null);
	}
}
