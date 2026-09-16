using FluentAssertions;
using FolderAssistant.Persistence;
using Microsoft.Data.Sqlite;

namespace FolderAssistant.Tests;

/// <summary>
/// The helper every test's scratch directory goes through. Its own failure mode is silent — a folder left
/// behind costs nothing visible until a disk fills — so it gets a test of its own.
/// </summary>
public sealed class TempFolderTests
{
	/// <summary>
	/// The factory's connections no longer pool (SPEC-130 0.14.0), so the database is opened here the way
	/// a test's own raw connection opens it — pooled, the driver's default — or this test would pass with
	/// the pool clearing removed and guard nothing.
	/// </summary>
	[Fact]
	public void A_Folder_Holding_A_Database_Still_Deletes_Itself()
	{
		String path;

		using (TempFolder folder = new())
		{
			path = folder.Path;
			String databasePath = new FolderDatabaseBootstrapper().EnsureInitialized(folder.Path, new PersistenceConfig()).DatabasePath;
			using SqliteConnection pooled = new($"Data Source={databasePath}");
			pooled.Open();
		}

		Directory.Exists(path).Should().BeFalse(
			"a disposed pooled connection goes back to the pool still holding the database file open, so the "
			+ "first delete fails and the pools have to be closed before trying again");
	}

	[Fact]
	public void An_Ordinary_Folder_Deletes_Itself()
	{
		String path;

		using (TempFolder folder = new())
		{
			path = folder.Path;
			File.WriteAllText(folder.Combine("note.md"), "content");
		}

		Directory.Exists(path).Should().BeFalse();
	}
}
