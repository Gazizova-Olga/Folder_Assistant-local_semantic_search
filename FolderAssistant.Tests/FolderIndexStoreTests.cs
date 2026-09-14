using FluentAssertions;
using FolderAssistant.Indexing.Outbox;
using FolderAssistant.Indexing.Scanning;
using FolderAssistant.Persistence;
using Microsoft.Data.Sqlite;

namespace FolderAssistant.Tests;

/// <summary>
/// The store the indexing library has been waiting on: file records on one side, queued deliveries
/// on the other, in one database.
///
/// <para>
/// Most of what is asserted here is what the store <em>does not</em> write. The delivery mark is the
/// column two writers can destroy for each other — a classification holding a snapshot, an insert
/// that believes a file is new — and the cost is not a wrong number but a file that is believed
/// indexed, never embedded, and silently absent from every search.
/// </para>
/// </summary>
public sealed class FolderIndexStoreTests
{
	private static readonly DateTime Created = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

	[Fact]
	public async Task A_Recorded_File_Comes_Back_As_It_Was_Recorded()
	{
		using TempFolder folder = new();
		FolderIndexStore store = StoreIn(folder);

		await store.ApplyAsync([Added("notes.md", "h1", size: 42)]);

		FileRecord? read = await store.ReadAsync("notes.md");

		read.Should().NotBeNull();
		read!.ContentHash.Should().Be("h1");
		read.Size.Should().Be(42);
		read.CreatedUtc.Should().Be(Created);

		IReadOnlyDictionary<String, FileRecord> all = await store.ReadAllAsync();

		all.Should().ContainKey("notes.md");
	}

	/// <summary>
	/// Recording a change and queuing its delivery are one write. Recorded without being queued, the
	/// change is believed and never delivered: the next comparison finds the record already matching
	/// the disk and concludes there is nothing to do.
	/// </summary>
	[Fact]
	public async Task Recording_A_Change_Queues_Its_Delivery()
	{
		using TempFolder folder = new();
		FolderIndexStore store = StoreIn(folder);

		await store.ApplyAsync([Added("notes.md", "h1")]);

		IReadOnlyList<OutboxOp> claimed = await store.ClaimDueAsync(10, DateTimeOffset.UtcNow, default);

		claimed.Should().ContainSingle();
		claimed[0].RelativePath.Should().Be("notes.md");
		claimed[0].Kind.Should().Be(DeliveryKind.Upsert);
	}

	/// <summary>
	/// A removed file leaves the index immediately and its row stays behind. The row is what its
	/// chunks hang from, and clearing those means clearing vectors first — the embedding side's job,
	/// which happens when the delivery queued here runs.
	/// </summary>
	[Fact]
	public async Task A_Removed_File_Leaves_The_Index_But_Keeps_Its_Place()
	{
		using TempFolder folder = new();
		FolderIndexStore store = StoreIn(folder);

		await store.ApplyAsync([Added("gone.md", "h1")]);
		await store.ApplyAsync([new ReconciledChange("gone.md", FileDelta.Removed, null)]);

		(await store.ReadAsync("gone.md")).Should().BeNull("a removed file is not part of the index");
		(await store.ReadAllAsync()).Should().NotContainKey("gone.md");

		Scalar(folder, "SELECT status FROM file_manifest WHERE file_path = 'gone.md';")
			.Should().Be("deleted", "the row outlives the file until its removal has been delivered");

		IReadOnlyList<OutboxOp> claimed = await store.ClaimDueAsync(10, DateTimeOffset.UtcNow, default);

		claimed.Should().ContainSingle(op => op.Kind == DeliveryKind.Delete);
	}

	/// <summary>
	/// Editing a file does not create it. The creation time is recorded once, and a later pass that
	/// knows nothing about it must not overwrite what is already there.
	/// </summary>
	[Fact]
	public async Task A_Creation_Time_Is_Recorded_Once_And_Kept()
	{
		using TempFolder folder = new();
		FolderIndexStore store = StoreIn(folder);

		await store.ApplyAsync([Added("notes.md", "h1")]);
		await store.ApplyAsync([
			new ReconciledChange("notes.md", FileDelta.Modified, new FileRecord("notes.md", "h2", 9, default)),
		]);

		FileRecord? read = await store.ReadAsync("notes.md");

		read!.CreatedUtc.Should().Be(Created);
		read.ContentHash.Should().Be("h2");
	}

	/// <summary>
	/// The mark says what was delivered, and a writer that only meant to insert must not clear it. A
	/// pass states <c>Added</c> because the index had no record when it looked; the row existing
	/// proves that belief stale, and the file may already be embedded. Taking the absence of a mark
	/// over a stored one un-marks an indexed file and costs a second embed of content nothing
	/// changed.
	/// </summary>
	[Fact]
	public async Task An_Insert_That_Turns_Out_To_Be_An_Update_Keeps_The_Mark_It_Never_Set()
	{
		using TempFolder folder = new();
		FolderIndexStore store = StoreIn(folder);

		await store.ApplyAsync([Added("notes.md", "h1")]);
		(await store.TryMarkSyncedAsync("notes.md", "h1", default)).Should().BeTrue();

		await store.ApplyAsync([Added("notes.md", "h1")]);

		DeliveryRecord? delivery = await store.ReadForDeliveryAsync("notes.md", default);

		delivery!.LastSyncedHash.Should().Be("h1", "nothing about the content changed, so nothing about the delivery did");
	}

	/// <summary>
	/// The one case where the mark must go. What it referred to left with the file's chunks when the
	/// removal was delivered, so a file restored byte-for-byte would look already delivered — and
	/// would never be embedded again, silently, for as long as it existed.
	/// </summary>
	[Fact]
	public async Task A_File_Restored_After_Its_Removal_Is_Not_Believed_Delivered()
	{
		using TempFolder folder = new();
		FolderIndexStore store = StoreIn(folder);

		await store.ApplyAsync([Added("notes.md", "h1")]);
		await store.TryMarkSyncedAsync("notes.md", "h1", default);

		await store.ApplyAsync([new ReconciledChange("notes.md", FileDelta.Removed, null)]);
		await store.ApplyAsync([Added("notes.md", "h1")]);

		DeliveryRecord? delivery = await store.ReadForDeliveryAsync("notes.md", default);

		delivery.Should().NotBeNull();
		delivery!.LastSyncedHash.Should().BeNull("what the mark referred to was removed with the file");
	}

	/// <summary>
	/// A delivery is a round trip, and the file can be rewritten during it. Recording the mark
	/// unconditionally would claim the newer content had been embedded when the older one was, after
	/// which the delivery queued for the newer content finds its work already done and skips.
	/// </summary>
	[Fact]
	public async Task Marking_Delivered_Requires_The_Content_That_Was_Delivered()
	{
		using TempFolder folder = new();
		FolderIndexStore store = StoreIn(folder);

		await store.ApplyAsync([Added("notes.md", "h1")]);

		// Rewritten while the delivery of h1 was in flight.
		await store.ApplyAsync([
			new ReconciledChange("notes.md", FileDelta.Modified, new FileRecord("notes.md", "h2", 9, Created)),
		]);

		(await store.TryMarkSyncedAsync("notes.md", "h1", default)).Should()
			.BeFalse("the content it was delivered for is no longer what the file holds");

		DeliveryRecord? delivery = await store.ReadForDeliveryAsync("notes.md", default);

		delivery!.LastSyncedHash.Should().BeNull();
	}

	/// <summary>
	/// The whole-folder pass marks a corpus delivered in one transaction, and each file is held to the
	/// same condition a single delivery is: only while the record still says what was delivered.
	/// </summary>
	[Fact]
	public async Task Marking_Many_Delivered_Holds_Each_To_The_Content_That_Was_Delivered()
	{
		using TempFolder folder = new();
		FolderIndexStore store = StoreIn(folder);

		await store.ApplyAsync([Added("a.md", "ha"), Added("b.md", "hb")]);

		// b.md moved on before the pass could say what it had embedded.
		await store.ApplyAsync([
			new ReconciledChange("b.md", FileDelta.Modified, new FileRecord("b.md", "hb2", 9, Created)),
		]);

		store.MarkSynced([new DeliveredContent("a.md", "ha"), new DeliveredContent("b.md", "hb")]).Should().Be(1);

		(await store.ReadForDeliveryAsync("a.md", default))!.LastSyncedHash.Should().Be("ha");
		(await store.ReadForDeliveryAsync("b.md", default))!.LastSyncedHash.Should().BeNull();
	}

	[Fact]
	public async Task Claiming_Takes_The_Oldest_Work_First_And_Does_Not_Hand_It_Out_Twice()
	{
		using TempFolder folder = new();
		FolderIndexStore store = StoreIn(folder);

		await store.ApplyAsync([Added("a.md", "h1")]);
		await store.ApplyAsync([Added("b.md", "h2")]);
		await store.ApplyAsync([Added("c.md", "h3")]);

		IReadOnlyList<OutboxOp> first = await store.ClaimDueAsync(2, DateTimeOffset.UtcNow, default);
		IReadOnlyList<OutboxOp> second = await store.ClaimDueAsync(2, DateTimeOffset.UtcNow, default);

		first.Select(op => op.RelativePath).Should().Equal("a.md", "b.md");
		second.Select(op => op.RelativePath).Should().Equal("c.md");
	}

	/// <summary>
	/// A failed delivery waits before it is tried again. Handing it back immediately would spend
	/// every attempt in the time the fault takes to notice.
	/// </summary>
	[Fact]
	public async Task Work_That_Is_Not_Due_Yet_Is_Not_Claimed()
	{
		using TempFolder folder = new();
		FolderIndexStore store = StoreIn(folder);

		await store.ApplyAsync([Added("notes.md", "h1")]);

		OutboxOp op = (await store.ClaimDueAsync(10, DateTimeOffset.UtcNow, default)).Single();

		await store.RescheduleAsync(op.Id, attempts: 1, DateTimeOffset.UtcNow.AddMinutes(5), "backend down", default);

		(await store.ClaimDueAsync(10, DateTimeOffset.UtcNow, default)).Should().BeEmpty();
		(await store.ClaimDueAsync(10, DateTimeOffset.UtcNow.AddMinutes(6), default)).Should()
			.ContainSingle(due => due.Attempts == 1);
	}

	/// <summary>
	/// A process that stopped mid-delivery recorded no outcome for what it was holding. Work nobody
	/// is delivering is work nobody will ever finish, so a start returns it to the queue.
	/// </summary>
	[Fact]
	public async Task Claimed_Work_Left_By_A_Stopped_Process_Returns_To_The_Queue()
	{
		using TempFolder folder = new();
		FolderIndexStore store = StoreIn(folder);

		await store.ApplyAsync([Added("notes.md", "h1")]);
		await store.ClaimDueAsync(10, DateTimeOffset.UtcNow, default);

		(await store.ClaimDueAsync(10, DateTimeOffset.UtcNow, default)).Should().BeEmpty("it is in flight");

		await store.RequeueInFlightAsync(default);

		(await store.ClaimDueAsync(10, DateTimeOffset.UtcNow, default)).Should().ContainSingle();
	}

	[Fact]
	public async Task Finished_Work_Is_Not_Handed_Out_Again()
	{
		using TempFolder folder = new();
		FolderIndexStore store = StoreIn(folder);

		await store.ApplyAsync([Added("notes.md", "h1")]);

		OutboxOp op = (await store.ClaimDueAsync(10, DateTimeOffset.UtcNow, default)).Single();

		await store.MarkDoneAsync(op.Id, default);
		await store.RequeueInFlightAsync(default);

		(await store.ClaimDueAsync(10, DateTimeOffset.UtcNow, default)).Should()
			.BeEmpty("finished is not in flight, and a requeue must not resurrect it");
	}

	/// <summary>
	/// Retiring abandoned work as done would leave a queue of things given up on indistinguishable
	/// from one where everything succeeded — and an abandoned delivery's only other symptom is a
	/// search that quietly does not find a file that is there.
	/// </summary>
	[Fact]
	public async Task Work_Given_Up_On_Is_Retired_As_Failed_And_Keeps_Its_Error()
	{
		using TempFolder folder = new();
		FolderIndexStore store = StoreIn(folder);

		await store.ApplyAsync([Added("notes.md", "h1")]);

		OutboxOp op = (await store.ClaimDueAsync(10, DateTimeOffset.UtcNow, default)).Single();

		await store.MarkAbandonedAsync(op.Id, attempts: 5, "HttpRequestException: no route to host", default);

		Scalar(folder, "SELECT status FROM outbox WHERE id = " + op.Id + ";").Should().Be(3L);
		Scalar(folder, "SELECT error FROM outbox WHERE id = " + op.Id + ";")
			.Should().Be("HttpRequestException: no route to host");

		(await store.ClaimDueAsync(10, DateTimeOffset.UtcNow, default)).Should().BeEmpty();
	}

	/// <summary>A change with no record to write is a programming error, not a file state.</summary>
	[Fact]
	public async Task An_Added_Change_Carrying_No_Record_Is_Refused()
	{
		using TempFolder folder = new();
		FolderIndexStore store = StoreIn(folder);

		Func<Task> apply = async () =>
			await store.ApplyAsync([new ReconciledChange("notes.md", FileDelta.Added, null)]);

		await apply.Should().ThrowAsync<ArgumentException>();
	}

	private static ReconciledChange Added(String path, String hash, Int64 size = 12)
		=> new(path, FileDelta.Added, new FileRecord(path, hash, size, Created));

	private static FolderIndexStore StoreIn(TempFolder folder)
	{
		DatabaseBootstrapResult database = new FolderDatabaseBootstrapper()
			.EnsureInitialized(folder.Path, new PersistenceConfig());

		return new FolderIndexStore(database.DatabasePath);
	}

	private static Object Scalar(TempFolder folder, String sql)
	{
		String databasePath = Path.Combine(folder.Path, ".folderassistant", "manifest.db");

		using SqliteConnection connection = FolderDatabaseConnection.OpenRead(databasePath);
		using SqliteCommand command = connection.CreateCommand();

		command.CommandText = sql;

		return command.ExecuteScalar()!;
	}
}
