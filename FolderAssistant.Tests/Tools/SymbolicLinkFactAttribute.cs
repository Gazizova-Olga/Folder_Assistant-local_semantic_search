namespace FolderAssistant.Tests.Tools;

/// <summary>
/// A fact that needs to create a file symbolic link. Windows grants that only to an elevated process or
/// one on a machine with Developer Mode on; a junction serves for directories but nothing unprivileged
/// serves for a file. Where the probe fails, the test is reported as skipped with the reason — visible in
/// the run's summary, and expected to be zero on CI, whose Windows runner is elevated.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class SymbolicLinkFactAttribute : FactAttribute
{
	private static readonly Lazy<String?> Reason = new(Probe);

	public SymbolicLinkFactAttribute()
	{
		if (Reason.Value is not null)
		{
			this.Skip = Reason.Value;
		}
	}

	private static String? Probe()
	{
		String target = Path.Combine(Path.GetTempPath(), $"folderassistant-symlink-probe-{Guid.NewGuid():N}");
		String link = target + ".link";
		try
		{
			File.WriteAllText(target, "");
			File.CreateSymbolicLink(link, target);

			return null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return $"This process cannot create a file symbolic link ({ex.Message.Trim()}); on Windows, turn on Developer Mode or run elevated.";
		}
		finally
		{
			File.Delete(link);
			File.Delete(target);
		}
	}
}
