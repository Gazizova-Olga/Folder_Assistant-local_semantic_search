using FluentAssertions;
using FolderAssistant.Indexing.Scanning;

namespace FolderAssistant.Tests.Indexing;

/// <summary>The creation-time rule as a function, independent of what any filesystem will report.</summary>
public sealed class FileTimestampsTests
{
	private static readonly DateTime Written = new(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

	[Fact]
	public void A_Reported_Creation_Time_Is_Recorded_As_Is()
	{
		DateTime created = new(2021, 11, 12, 13, 14, 15, DateTimeKind.Utc);

		FileTimestamps.ResolveCreatedUtc(created, Written).Should().Be(created);
	}

	/// <summary>
	/// The file API reports the 1601 file-time epoch when it has no creation time to give. Recorded as-is,
	/// every such file would share one impossible creation date.
	/// </summary>
	[Fact]
	public void Where_No_Creation_Time_Is_Reported_The_Write_Time_Stands_In()
	{
		FileTimestamps.ResolveCreatedUtc(DateTime.FromFileTimeUtc(0), Written).Should().Be(Written);
	}

	[Fact]
	public void A_Modified_Record_Takes_The_Recorded_Creation_Time_And_Everything_Else_From_The_Disk()
	{
		DateTime recordedCreation = new(2015, 6, 7, 8, 9, 10, DateTimeKind.Utc);
		FileRecord recorded = new("doc.md", "old-hash", 10, recordedCreation);
		FileRecord current = new("doc.md", "new-hash", 20, Written);

		FileTimestamps.KeepRecordedCreation(current, recorded)
			.Should().Be(new FileRecord("doc.md", "new-hash", 20, recordedCreation));
	}
}

/// <summary>Backdating for the tests that tell a file's creation time apart from the time a writer ran.</summary>
internal static class TimestampFixture
{
	public static readonly DateTime LongAgo = new(2020, 3, 4, 5, 6, 7, DateTimeKind.Utc);

	/// <summary>
	/// Best-effort. Not every platform lets a creation time be set, and the tests do not depend on it:
	/// they compare exactly against what the filesystem reports. Backdating only makes a coincidence
	/// with the writer's clock impossible rather than merely improbable.
	/// </summary>
	public static void TryBackdate(string path)
	{
		try
		{
			File.SetCreationTimeUtc(path, LongAgo);
		}
		catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
		{
			// Left at its real creation time.
		}
	}

	/// <summary>
	/// The comparison against what the filesystem reports uses the rule itself as the expected value, so
	/// it cannot see the rule being wrong. Where the backdating took, this checks against a value the rule
	/// did not compute.
	/// </summary>
	public static void ShouldBeTheBackdatedTimeWhereItTook(string path, DateTime recorded)
	{
		if (File.GetCreationTimeUtc(path) == LongAgo)
		{
			recorded.Should().Be(LongAgo);
		}
	}
}
