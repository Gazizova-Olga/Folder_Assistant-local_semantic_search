using FluentAssertions;
using FolderAssistant.Tools;

namespace FolderAssistant.Tests.Tools;

/// <summary>
/// The containment rule (SPEC-101), tested directly and never through a tool: each way a textual check can
/// be defeated is staged on disk and asserted refused, and each fixture that cannot be staged fails the
/// test rather than letting it pass with nothing asserted.
/// </summary>
public sealed class WorkspacePathGuardTests
{
	private const String Metadata = ".folderassistant";

	// --- the textual rule ---------------------------------------------------------------------------

	[Fact]
	public void A_Relative_Path_Resolves_Under_The_Root()
	{
		using TempFolder root = new();
		WorkspacePathGuard guard = new(root.Path, Metadata);

		GuardedPath resolved = guard.Resolve(Path.Combine("docs", "readme.md"));

		resolved.FullPath.Should().Be(root.Combine("docs", "readme.md"));
		resolved.RelativePath.Should().Be(Path.Combine("docs", "readme.md"));
		resolved.Note.Should().BeNull();
	}

	[Fact]
	public void An_Absolute_Path_Inside_The_Root_Resolves()
	{
		using TempFolder root = new();
		WorkspacePathGuard guard = new(root.Path, Metadata);

		GuardedPath resolved = guard.Resolve(root.Combine("a.txt"));

		resolved.RelativePath.Should().Be("a.txt");
	}

	[Fact]
	public void The_Root_Itself_Resolves_With_An_Empty_Relative_Path()
	{
		using TempFolder root = new();
		WorkspacePathGuard guard = new(root.Path, Metadata);

		guard.Resolve(".").RelativePath.Should().BeEmpty();
		guard.Resolve(root.Path + Path.DirectorySeparatorChar).RelativePath.Should().BeEmpty();
	}

	[Fact]
	public void A_Collapsed_Parent_Segment_That_Stays_Inside_Resolves()
	{
		using TempFolder root = new();
		WorkspacePathGuard guard = new(root.Path, Metadata);

		guard.Resolve(Path.Combine("sub", "..", "file.txt")).RelativePath.Should().Be("file.txt");
	}

	[Fact]
	public void A_Parent_Segment_That_Leaves_The_Root_Is_Refused()
	{
		using TempFolder root = new();
		WorkspacePathGuard guard = new(root.Path, Metadata);

		Refusal(guard, Path.Combine("..", "outside.txt")).Should().Be(ContainmentRefusal.OutsideRoot);
		Refusal(guard, Path.Combine("sub", "..", "..", "outside.txt")).Should().Be(ContainmentRefusal.OutsideRoot);
	}

	[Fact]
	public void A_Sibling_Sharing_The_Roots_Prefix_Is_Refused()
	{
		using TempFolder root = new();
		WorkspacePathGuard guard = new(root.Path, Metadata);

		Refusal(guard, root.Path + "2" + Path.DirectorySeparatorChar + "file.txt")
			.Should().Be(ContainmentRefusal.OutsideRoot);
	}

	[Fact]
	public void An_Absolute_Path_Elsewhere_Is_Refused()
	{
		using TempFolder root = new();
		using TempFolder elsewhere = new();
		WorkspacePathGuard guard = new(root.Path, Metadata);

		Refusal(guard, elsewhere.Combine("file.txt")).Should().Be(ContainmentRefusal.OutsideRoot);
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void An_Empty_Path_Is_Refused(String path)
	{
		using TempFolder root = new();
		WorkspacePathGuard guard = new(root.Path, Metadata);

		Refusal(guard, path).Should().Be(ContainmentRefusal.EmptyPath);
	}

	[Theory]
	[InlineData(@"\\?\")]
	[InlineData(@"\\.\")]
	[InlineData("//?/")]
	public void Device_Path_Syntax_Is_Refused_Before_Normalisation(String prefix)
	{
		using TempFolder root = new();
		WorkspacePathGuard guard = new(root.Path, Metadata);

		Refusal(guard, prefix + root.Combine("file.txt")).Should().Be(ContainmentRefusal.DevicePath);
	}

	[Fact]
	public void A_Drive_Root_Works_As_The_Workspace_Root()
	{
		using TempFolder folder = new();
		String driveRoot = Path.GetPathRoot(folder.Path)!;
		WorkspacePathGuard guard = new(driveRoot, Metadata);

		GuardedPath resolved = guard.Resolve(folder.Combine("file.txt"));

		guard.Root.Should().Be(driveRoot);
		resolved.RelativePath.Should().Be(Path.GetRelativePath(driveRoot, folder.Combine("file.txt")));
		guard.Resolve(driveRoot).RelativePath.Should().BeEmpty();
	}

	// --- the metadata folder ------------------------------------------------------------------------

	[Fact]
	public void The_Metadata_Folder_And_Its_Contents_Are_Refused()
	{
		using TempFolder root = new();
		WorkspacePathGuard guard = new(root.Path, Metadata);

		Refusal(guard, Metadata).Should().Be(ContainmentRefusal.MetadataFolder);
		Refusal(guard, Path.Combine(Metadata, "manifest.db")).Should().Be(ContainmentRefusal.MetadataFolder);
		Refusal(guard, root.Combine(Metadata, "manifest.db-wal")).Should().Be(ContainmentRefusal.MetadataFolder);
	}

	[Fact]
	public void A_Same_Named_Folder_Deeper_In_The_Tree_Is_Not_The_Metadata_Folder()
	{
		using TempFolder root = new();
		WorkspacePathGuard guard = new(root.Path, Metadata);

		guard.Resolve(Path.Combine("project", Metadata, "notes.txt")).RelativePath
			.Should().Be(Path.Combine("project", Metadata, "notes.txt"));
		guard.IsMetadataFolder(root.Combine("project", Metadata)).Should().BeFalse();
	}

	[Fact]
	public void IsMetadataFolder_Answers_For_Listings()
	{
		using TempFolder root = new();
		WorkspacePathGuard guard = new(root.Path, Metadata);

		guard.IsMetadataFolder(root.Combine(Metadata)).Should().BeTrue();
		guard.IsMetadataFolder(root.Combine(Metadata, "manifest.db")).Should().BeTrue();
		guard.IsMetadataFolder(root.Combine("docs")).Should().BeFalse();
		guard.IsMetadataFolder(root.Combine(Metadata + "-not")).Should().BeFalse();
	}

	// --- reparse points -----------------------------------------------------------------------------

	[Fact]
	public void A_Link_At_The_Last_Segment_Is_Refused()
	{
		using TempFolder root = new();
		using TempFolder outside = new();
		String link = root.Combine("escape");
		LinkFixtures.CreateDirectoryLink(link, outside.Path);
		WorkspacePathGuard guard = new(root.Path, Metadata);

		Refusal(guard, "escape").Should().Be(ContainmentRefusal.ReparsePoint);
	}

	[Fact]
	public void A_Link_At_An_Intermediate_Segment_Is_Refused()
	{
		using TempFolder root = new();
		using TempFolder outside = new();
		File.WriteAllText(outside.Combine("secret.txt"), "outside");
		String link = root.Combine("escape");
		LinkFixtures.CreateDirectoryLink(link, outside.Path);
		WorkspacePathGuard guard = new(root.Path, Metadata);

		Refusal(guard, Path.Combine("escape", "secret.txt")).Should().Be(ContainmentRefusal.ReparsePoint);
		Refusal(guard, Path.Combine("escape", "missing", "deeper.txt")).Should().Be(ContainmentRefusal.ReparsePoint);
	}

	[Fact]
	public void A_Link_Pointing_Inside_The_Root_Is_Still_Refused()
	{
		// The rule is about redirection, not destination: what a link resolves to can change after the
		// check, so a link is refused for being one.
		using TempFolder root = new();
		Directory.CreateDirectory(root.Combine("real"));
		LinkFixtures.CreateDirectoryLink(root.Combine("alias"), root.Combine("real"));
		WorkspacePathGuard guard = new(root.Path, Metadata);

		Refusal(guard, Path.Combine("alias", "file.txt")).Should().Be(ContainmentRefusal.ReparsePoint);
		guard.Resolve(Path.Combine("real", "file.txt")).RelativePath.Should().Be(Path.Combine("real", "file.txt"));
	}

	[Fact]
	public void A_Plain_Directory_Beside_A_Link_Resolves()
	{
		using TempFolder root = new();
		using TempFolder outside = new();
		Directory.CreateDirectory(root.Combine("plain"));
		LinkFixtures.CreateDirectoryLink(root.Combine("escape"), outside.Path);
		WorkspacePathGuard guard = new(root.Path, Metadata);

		guard.Resolve(Path.Combine("plain", "file.txt")).RelativePath.Should().Be(Path.Combine("plain", "file.txt"));
	}

	[Fact]
	public void A_Missing_Path_Below_The_Root_Resolves()
	{
		// A create target does not exist yet; nothing that does not exist can redirect.
		using TempFolder root = new();
		WorkspacePathGuard guard = new(root.Path, Metadata);

		guard.Resolve(Path.Combine("new", "deeper", "file.txt")).RelativePath
			.Should().Be(Path.Combine("new", "deeper", "file.txt"));
	}

	// --- hard links ---------------------------------------------------------------------------------

	[Fact]
	public void A_Hard_Link_To_A_File_Outside_The_Root_Is_Refused_On_Windows_And_Allowed_Elsewhere()
	{
		using TempFolder root = new();
		using TempFolder outside = new();
		String original = outside.Combine("original.txt");
		File.WriteAllText(original, "outside");
		String link = root.Combine("linked.txt");
		LinkFixtures.CreateHardLink(link, original);
		WorkspacePathGuard guard = new(root.Path, Metadata);

		if (OperatingSystem.IsWindows())
		{
			Refusal(guard, "linked.txt").Should().Be(ContainmentRefusal.HardLinkOutsideRoot);
		}
		else
		{
			guard.Resolve("linked.txt").RelativePath.Should().Be("linked.txt");
		}
	}

	[Fact]
	public void A_Hard_Link_Whose_Every_Name_Is_Inside_The_Root_Resolves()
	{
		using TempFolder root = new();
		String original = root.Combine("original.txt");
		File.WriteAllText(original, "inside");
		LinkFixtures.CreateHardLink(root.Combine("linked.txt"), original);
		WorkspacePathGuard guard = new(root.Path, Metadata);

		guard.Resolve("linked.txt").Note.Should().BeNull();
		guard.Resolve("linked.txt").RelativePath.Should().Be("linked.txt");
	}

	// --- subst drives (Windows) ---------------------------------------------------------------------

	[WindowsFact]
	public void A_Subst_Drive_Over_The_Root_Accepts_A_Path_Through_The_Letter()
	{
		using TempFolder root = new();
		using SubstDrive drive = new(root.Path);
		WorkspacePathGuard guard = new(root.Path, Metadata);

		GuardedPath resolved = guard.Resolve(drive.Letter + @"\docs\file.txt");

		resolved.FullPath.Should().Be(drive.Letter + @"\docs\file.txt");
		resolved.RelativePath.Should().Be(@"docs\file.txt");
		resolved.Note.Should().BeNull();
	}

	[WindowsFact]
	public void A_Root_On_A_Subst_Drive_Accepts_The_Physical_Path()
	{
		using TempFolder folder = new();
		using SubstDrive drive = new(folder.Path);
		WorkspacePathGuard guard = new(drive.Letter + @"\", Metadata);

		GuardedPath physical = guard.Resolve(folder.Combine("file.txt"));
		GuardedPath lettered = guard.Resolve(drive.Letter + @"\a\b.txt");

		physical.RelativePath.Should().Be("file.txt");
		physical.Note.Should().BeNull();
		lettered.RelativePath.Should().Be(@"a\b.txt");
		Refusal(guard, Path.Combine(Path.GetTempPath(), "elsewhere.txt")).Should().Be(ContainmentRefusal.OutsideRoot);
	}

	[WindowsFact]
	public void A_Subst_Drive_Mapped_Outside_The_Root_Is_Refused()
	{
		using TempFolder root = new();
		using TempFolder outside = new();
		using SubstDrive drive = new(outside.Path);
		WorkspacePathGuard guard = new(root.Path, Metadata);

		Refusal(guard, drive.Letter + @"\file.txt").Should().Be(ContainmentRefusal.OutsideRoot);
	}

	[WindowsFact]
	public void A_Subst_Drive_Mapped_Inside_The_Root_Is_Seen_Through()
	{
		using TempFolder root = new();
		Directory.CreateDirectory(root.Combine("sub"));
		using SubstDrive drive = new(root.Combine("sub"));
		WorkspacePathGuard guard = new(root.Path, Metadata);

		GuardedPath resolved = guard.Resolve(drive.Letter + @"\file.txt");

		resolved.RelativePath.Should().Be(@"sub\file.txt");
		resolved.Note.Should().BeNull();
	}

	[WindowsFact]
	public void A_Chain_Of_Substitutions_Leaves_The_Textual_Rule_Deciding_And_Says_So()
	{
		using TempFolder folder = new();
		Directory.CreateDirectory(folder.Combine("sub"));
		using SubstDrive first = new(folder.Path);
		using SubstDrive second = new(first.Letter + @"\sub");
		WorkspacePathGuard guard = new(second.Letter + @"\", Metadata);

		GuardedPath textual = guard.Resolve(second.Letter + @"\file.txt");

		textual.RelativePath.Should().Be("file.txt");
		textual.Note.Should().NotBeNull().And.Contain("not verified");
		// Physically inside, but the chain is not followed on trust: textually it is another drive.
		Refusal(guard, folder.Combine("sub", "file.txt")).Should().Be(ContainmentRefusal.OutsideRoot);
	}

	// --- fixtures -----------------------------------------------------------------------------------

	private static ContainmentRefusal Refusal(WorkspacePathGuard guard, String path)
	{
		Action act = () => guard.Resolve(path);

		return act.Should().Throw<WorkspaceContainmentException>().Which.Refusal;
	}

	/// <summary>
	/// Claims a free drive letter with <c>subst</c> for the life of the test and releases it after. A letter
	/// that cannot be claimed throws: the test fails, it does not pass with nothing asserted.
	/// </summary>
	private sealed class SubstDrive : IDisposable
	{
		public SubstDrive(String target)
		{
			HashSet<Char> taken = DriveInfo.GetDrives()
				.Select(drive => Char.ToUpperInvariant(drive.Name[0]))
				.ToHashSet();

			for (Char letter = 'Z'; letter >= 'D'; letter--)
			{
				if (taken.Contains(letter) || Directory.Exists($"{letter}:\\"))
				{
					continue;
				}

				LinkFixtures.Run("subst", $"{letter}:", target);
				if (!Directory.Exists($"{letter}:\\"))
				{
					throw new InvalidOperationException($"Fixture could not be staged: subst {letter}: did not appear.");
				}

				this.Letter = $"{letter}:";

				return;
			}

			throw new InvalidOperationException("Fixture could not be staged: no free drive letter for subst.");
		}

		public String Letter { get; }

		public void Dispose()
		{
			LinkFixtures.Run("subst", this.Letter, "/D");
		}
	}
}
