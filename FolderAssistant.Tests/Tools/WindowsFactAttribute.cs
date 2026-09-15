namespace FolderAssistant.Tests.Tools;

/// <summary>
/// A fact whose subject exists only on Windows — a <c>subst</c> drive, say. Elsewhere it is reported as
/// skipped with this reason, which is different from a test that runs and asserts nothing: a skip is
/// visible in the run's summary, and the count of them is expected to be zero on the platform the tree
/// is developed on.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class WindowsFactAttribute : FactAttribute
{
	public WindowsFactAttribute()
	{
		if (!OperatingSystem.IsWindows())
		{
			this.Skip = "The subject of this test exists only on Windows.";
		}
	}
}
