using FluentAssertions;
using FolderAssistant.Indexing.Watching;

namespace FolderAssistant.Tests.Indexing;

/// <summary>
/// What the watcher refuses to report. Asserted as a function rather than through a real watcher,
/// which would test the operating system's timing alongside the rule.
/// </summary>
public sealed class IndexablePathFilterTests
{
	private static readonly IndexablePathFilter Filter = new(".folderassistant");

	[Theory]
	[InlineData("work/notes.md")]
	[InlineData("work/deep/nested/notes.md")]
	[InlineData("work/obj-lesson.md")]
	[InlineData("work/binder.md")]
	public void An_Ordinary_File_Is_Reported(string path)
		=> Filter.ShouldReport(Rooted(path)).Should().BeTrue();

	/// <summary>
	/// Not an optimisation. The index lives inside the folder it indexes, so its own writes land
	/// under the watched tree — unfiltered, indexing causes an event which causes indexing, with no
	/// idle state to settle into.
	/// </summary>
	[Theory]
	[InlineData("work/.folderassistant/manifest.db")]
	[InlineData("work/.folderassistant/manifest.db-wal")]
	public void The_Metadata_Folder_Is_Never_Reported(string path)
		=> Filter.ShouldReport(Rooted(path)).Should().BeFalse();

	[Theory]
	[InlineData("work/bin/app.dll")]
	[InlineData("work/obj/Debug/app.pdb")]
	[InlineData("work/.git/HEAD")]
	[InlineData("work/.vs/config")]
	[InlineData("work/node_modules/pkg/index.js")]
	[InlineData("work/deep/bin/nested.txt")]
	public void Build_Output_And_Tooling_Directories_Are_Not_Reported(string path)
		=> Filter.ShouldReport(Rooted(path)).Should().BeFalse();

	/// <summary>
	/// The transient file an atomic write leaves beside its target. It holds a half-written copy of a
	/// document that is about to be reported in its own right.
	/// </summary>
	[Theory]
	[InlineData("work/notes.md.tmp")]
	[InlineData("work/a1b2c3.tmp")]
	public void A_Temporary_Write_File_Is_Not_Reported(string path)
		=> Filter.ShouldReport(Rooted(path)).Should().BeFalse();

	/// <summary>
	/// The configured name is honoured, not just the default one. A deployment that renamed its
	/// metadata folder would otherwise have the index watching its own database.
	/// </summary>
	[Fact]
	public void A_Configured_Metadata_Folder_Name_Is_Honoured_Instead_Of_The_Default()
	{
		IndexablePathFilter configured = new("_index");

		configured.ShouldReport(Rooted("work/_index/manifest.db")).Should().BeFalse();
		configured.ShouldReport(Rooted("work/.folderassistant/notes.md")).Should().BeTrue();
	}

	/// <summary>
	/// A directory whose name merely starts with an excluded one is a different directory. Matching
	/// on a prefix rather than a whole segment would silently stop indexing a folder called
	/// "binaries" or "objects".
	/// </summary>
	[Theory]
	[InlineData("work/binaries/notes.md")]
	[InlineData("work/objects/notes.md")]
	[InlineData("work/node_modules_old/notes.md")]
	public void A_Directory_Merely_Prefixed_By_An_Excluded_Name_Is_Still_Reported(string path)
		=> Filter.ShouldReport(Rooted(path)).Should().BeTrue();

	/// <summary>
	/// Given the extensions this system reads, everything else is not part of the corpus and is never
	/// reported — the alternative is recording and delivering every binary in the folder to a consumer
	/// that refuses each one. Case-insensitive, as the application's own allow-list is.
	/// </summary>
	[Theory]
	[InlineData("work/notes.md", true)]
	[InlineData("work/NOTES.MD", true)]
	[InlineData("work/deep/readme.txt", true)]
	[InlineData("work/photo.png", false)]
	[InlineData("work/archive.zip", false)]
	[InlineData("work/Makefile", false)]
	public void With_An_Extension_List_Only_Those_Extensions_Are_Reported(string path, bool reported)
	{
		IndexablePathFilter limited = new(".folderassistant", indexableExtensions: [".md", ".txt"]);

		limited.ShouldReport(Rooted(path)).Should().Be(reported);
	}

	[Fact]
	public void Without_An_Extension_List_Every_Extension_Is_Reported()
		=> Filter.ShouldReport(Rooted("work/photo.png")).Should().BeTrue();

	/// <summary>
	/// Size is a property of a file on disk, so it is a separate question from the path's; a file over
	/// the bound is not indexed, and one exactly on it is.
	/// </summary>
	[Fact]
	public void A_File_Over_The_Size_Bound_Is_Not_Indexed_And_One_On_It_Is()
	{
		IndexablePathFilter bounded = new(".folderassistant", maxContentBytes: 10);

		bounded.ShouldIndex(Rooted("work/notes.md"), 11).Should().BeFalse();
		bounded.ShouldIndex(Rooted("work/notes.md"), 10).Should().BeTrue();
	}

	[Fact]
	public void A_Small_File_On_An_Unreported_Path_Is_Still_Not_Indexed()
		=> Filter.ShouldIndex(Rooted("work/bin/notes.md"), 1).Should().BeFalse();

	[Fact]
	public void A_Negative_Size_Bound_Is_Rejected()
	{
		Action construct = () => _ = new IndexablePathFilter(".folderassistant", maxContentBytes: -1);

		construct.Should().Throw<ArgumentOutOfRangeException>();
	}

	/// <summary>
	/// The tables above are written with '/' and rooted here in the platform's own form, because the
	/// filter splits on the platform's separators: on Linux a Windows-literal path is one segment with
	/// no directory in it, and every exclusion row passes as an ordinary file.
	/// </summary>
	private static string Rooted(string path)
		=> OperatingSystem.IsWindows() ? @"C:\" + path.Replace('/', '\\') : "/" + path;
}
