namespace FolderAssistant.Tests;

/// <summary>A directory that deletes itself when the test finishes.</summary>
internal sealed class TempFolder : IDisposable
{
	public TempFolder()
	{
		this.Path = System.IO.Path.Combine(
			System.IO.Path.GetTempPath(),
			"folderassistant-tests",
			Guid.NewGuid().ToString("N"));

		Directory.CreateDirectory(this.Path);
	}

	/// <summary>The absolute path of the directory.</summary>
	public String Path { get; }

	/// <summary>Combines <paramref name="parts"/> onto <see cref="Path"/>.</summary>
	public String Combine(params String[] parts)
		=> System.IO.Path.Combine([this.Path, .. parts]);

	public void Dispose()
	{
		try
		{
			Directory.Delete(this.Path, recursive: true);
		}
		catch (IOException)
		{
			// A file still held open by a connection the test did not dispose. Leaving a temp directory
			// behind is not worth failing a test that otherwise passed, and it says nothing about the
			// behaviour under test — the process exit will not clean it up, but the OS eventually does.
		}
		catch (UnauthorizedAccessException)
		{
			// Same reasoning: on Windows a delete blocked by an open handle surfaces this way instead.
		}
	}
}
