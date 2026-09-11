using FluentAssertions;
using FolderAssistant.Persistence;

namespace FolderAssistant.Tests;

/// <summary>
/// The helper every test's scratch directory goes through. Its own failure mode is silent — a folder left
/// behind costs nothing visible until a disk fills — so it gets a test of its own.
/// </summary>
public sealed class TempFolderTests
{
	[Fact]
	public void A_Folder_Holding_A_Database_Still_Deletes_Itself()
	{
		String path;

		using (TempFolder folder = new())
		{
			path = folder.Path;
			new FolderDatabaseBootstrapper().EnsureInitialized(folder.Path, new PersistenceConfig());
		}

		Directory.Exists(path).Should().BeFalse(
			"a disposed connection goes back to a pool still holding the database file open, so the first "
			+ "delete fails and the pools have to be closed before trying again");
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
