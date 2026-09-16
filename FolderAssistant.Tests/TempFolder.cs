using Microsoft.Data.Sqlite;

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

	/// <summary>
	/// Deletes the directory, and if a database file is still held open, closes the pooled connections
	/// holding it and tries once more.
	///
	/// <para>
	/// A disposed pooled <see cref="SqliteConnection"/> does not close its handle: it goes back to a pool
	/// keyed on the connection string, keeping the file open and the directory undeletable. The
	/// application's factory opens without the pool (SPEC-130 0.14.0); the raw connections tests open for
	/// themselves still pool, which is why this stays. Every test that touches a
	/// database therefore used to leave its folder behind — 31,360 of them, 5.9 GB, before this was noticed.
	/// The first failure is silent and the cost only shows up as a full disk much later, which is why the
	/// retry is here rather than a comment saying the operating system will get round to it.
	/// </para>
	/// </summary>
	public void Dispose()
	{
		if (TryDelete())
		{
			return;
		}

		SqliteConnection.ClearAllPools();
		TryDelete();
	}

	private Boolean TryDelete()
	{
		try
		{
			Directory.Delete(this.Path, recursive: true);

			return true;
		}
		catch (DirectoryNotFoundException)
		{
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Still held — by a writer the test left running, say. Failing a test that otherwise passed over
			// a temp directory would say nothing about the behaviour under test. On Windows a delete blocked
			// by an open handle surfaces either way, which is why both are caught.
			return false;
		}
	}
}
