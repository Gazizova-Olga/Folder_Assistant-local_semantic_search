using System.Diagnostics;

namespace FolderAssistant.Tests.Tools;

/// <summary>
/// Links and hard links staged on disk for the containment tests. Every helper throws when the thing it
/// was asked for is not there afterwards, so a test whose fixture could not be staged fails instead of
/// asserting against a plain directory or file and passing.
/// </summary>
internal static class LinkFixtures
{
	/// <summary>A junction on Windows (no privilege needed) or a symbolic link elsewhere.</summary>
	public static void CreateDirectoryLink(String link, String target)
	{
		if (OperatingSystem.IsWindows())
		{
			Run("cmd.exe", "/c", "mklink", "/J", link, target);
		}
		else
		{
			Directory.CreateSymbolicLink(link, target);
		}

		if (new DirectoryInfo(link).LinkTarget is null)
		{
			throw new InvalidOperationException($"Fixture could not be staged: '{link}' is not a link.");
		}
	}

	/// <summary>A file symbolic link. On Windows this needs Developer Mode or a privilege; without it the fixture fails.</summary>
	public static void CreateFileLink(String link, String target)
	{
		File.CreateSymbolicLink(link, target);

		if (new FileInfo(link).LinkTarget is null)
		{
			throw new InvalidOperationException($"Fixture could not be staged: '{link}' is not a link.");
		}
	}

	public static void CreateHardLink(String link, String target)
	{
		if (OperatingSystem.IsWindows())
		{
			Run("cmd.exe", "/c", "mklink", "/H", link, target);
		}
		else
		{
			Run("ln", target, link);
		}

		if (!File.Exists(link))
		{
			throw new InvalidOperationException($"Fixture could not be staged: hard link '{link}' was not created.");
		}
	}

	public static void Run(String fileName, params String[] arguments)
	{
		ProcessStartInfo start = new(fileName)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		foreach (String argument in arguments)
		{
			start.ArgumentList.Add(argument);
		}

		using Process process = Process.Start(start)
			?? throw new InvalidOperationException($"Fixture could not be staged: '{fileName}' did not start.");
		String error = process.StandardError.ReadToEnd();
		String output = process.StandardOutput.ReadToEnd();
		process.WaitForExit();

		if (process.ExitCode != 0)
		{
			throw new InvalidOperationException(
				$"Fixture could not be staged: '{fileName} {String.Join(' ', arguments)}' exited {process.ExitCode}: {error}{output}");
		}
	}
}
