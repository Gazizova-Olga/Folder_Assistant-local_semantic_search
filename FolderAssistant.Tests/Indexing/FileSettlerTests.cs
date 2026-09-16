using FluentAssertions;
using FolderAssistant.Indexing.Pipeline;

namespace FolderAssistant.Tests.Indexing;

/// <summary>
/// The settle probe. The interval between its two looks is replaced by a step the test controls, so a
/// write landing between them is staged exactly rather than raced against a real delay.
/// </summary>
public sealed class FileSettlerTests
{
	[Fact]
	public async Task A_Quiet_Readable_File_Is_Settled()
	{
		using TempFolder folder = new();
		string path = folder.Combine("quiet.md");
		await File.WriteAllTextAsync(path, "done");

		SettleResult result = await Settler(betweenLooks: () => { }).SettleAsync(path, CancellationToken.None);

		result.Should().Be(SettleResult.Settled);
	}

	[Fact]
	public async Task A_File_That_Is_Not_There_Is_Missing()
	{
		using TempFolder folder = new();

		SettleResult result = await Settler(betweenLooks: () => { })
			.SettleAsync(folder.Combine("absent.md"), CancellationToken.None);

		result.Should().Be(SettleResult.Missing);
	}

	/// <summary>
	/// A writer holding its handle denies the share-<c>Read</c> open on Windows, which is how every read on
	/// this path is made — the file is busy, not broken. Elsewhere share modes are advisory, the open
	/// succeeds, and a held file whose size and write time hold still is settled: the second look is the
	/// only protection there. One test asserting each platform's documented result, never a skip.
	/// </summary>
	[Fact]
	public async Task A_File_Held_By_A_Writer_Is_Busy_Where_Share_Modes_Are_Enforced()
	{
		using TempFolder folder = new();
		string path = folder.Combine("held.md");
		await File.WriteAllTextAsync(path, "in progress");

		await using FileStream writer = new(path, FileMode.Open, FileAccess.Write, FileShare.Read);

		SettleResult result = await Settler(betweenLooks: () => { }).SettleAsync(path, CancellationToken.None);

		result.Should().Be(OperatingSystem.IsWindows() ? SettleResult.Busy : SettleResult.Settled);
	}

	/// <summary>
	/// A writer that shares the file passes the open, so only the second look can see it is still moving.
	/// </summary>
	[Fact]
	public async Task A_File_That_Grows_Between_The_Two_Looks_Is_Busy()
	{
		using TempFolder folder = new();
		string path = folder.Combine("growing.log");
		await File.WriteAllTextAsync(path, "first line\n");

		SettleResult result = await Settler(betweenLooks: () => File.AppendAllText(path, "second line\n"))
			.SettleAsync(path, CancellationToken.None);

		result.Should().Be(SettleResult.Busy);
	}

	[Fact]
	public async Task A_File_Deleted_Between_The_Two_Looks_Is_Missing()
	{
		using TempFolder folder = new();
		string path = folder.Combine("fleeting.md");
		await File.WriteAllTextAsync(path, "brief");

		SettleResult result = await Settler(betweenLooks: () => File.Delete(path))
			.SettleAsync(path, CancellationToken.None);

		result.Should().Be(SettleResult.Missing);
	}

	private static FileSettler Settler(Action betweenLooks)
		=> new(TimeSpan.Zero, (_, _) =>
		{
			betweenLooks();
			return Task.CompletedTask;
		});
}
