using System.Collections.Concurrent;
using FluentAssertions;
using FolderAssistant.Indexing.Outbox;
using Microsoft.Extensions.Logging;
using FolderAssistant.Indexing.Scanning;

namespace FolderAssistant.Tests.Indexing;

/// <summary>
/// The dispatcher against an in-memory outbox, a scripted embedding side and a clock the test moves.
///
/// <para>
/// Retry timing is asserted by advancing the clock, never by sleeping, so a backoff is checked as the value
/// it is rather than as whatever the machine managed in the time allowed.
/// </para>
/// </summary>
public sealed class OutboxDispatcherTests
{
	[Fact]
	public async Task An_Upsert_Delivers_The_Files_Content_Under_Its_File_Id_And_Records_It_Delivered()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("note.md"), "the content");

		DateTime created = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
		InMemoryOutbox outbox = new();
		outbox.Record("note.md", "hash-1", size: 11, createdUtc: created);
		long op = outbox.Enqueue("note.md", DeliveryKind.Upsert);

		ScriptedVectorizer vectorizer = new();

		await Dispatcher(folder, outbox, vectorizer).DrainOnceAsync(CancellationToken.None);

		Delivery delivery = vectorizer.Upserts.Should().ContainSingle().Subject;
		delivery.DocId.Should().Be(FileIdentity.For("note.md"));
		delivery.Content.Should().Be("the content");
		delivery.Metadata.Should().Be(new FileMetadata(folder.Combine("note.md"), 11, created, ".md", "hash-1"));

		outbox.LastSyncedHash("note.md").Should().Be("hash-1");
		outbox.StateOf(op).Should().Be(OpState.Done);
	}

	[Fact]
	public async Task A_Delete_Is_Delivered_Under_The_Same_File_Id()
	{
		using TempFolder folder = new();
		InMemoryOutbox outbox = new();
		long op = outbox.Enqueue(Path.Combine("docs", "gone.md").Replace('\\', '/'), DeliveryKind.Delete);

		ScriptedVectorizer vectorizer = new();

		await Dispatcher(folder, outbox, vectorizer).DrainOnceAsync(CancellationToken.None);

		vectorizer.Deletes.Should().ContainSingle().Which.Should().Be(FileIdentity.For("docs/gone.md"));
		outbox.StateOf(op).Should().Be(OpState.Done);
	}

	/// <summary>
	/// Delivery is at-least-once, so a redelivery must cost nothing: content already delivered is not sent
	/// again.
	/// </summary>
	[Fact]
	public async Task Content_Already_Delivered_Is_Not_Delivered_Again()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("note.md"), "the content");

		InMemoryOutbox outbox = new();
		outbox.Record("note.md", "hash-1", syncedHash: "hash-1");
		long op = outbox.Enqueue("note.md", DeliveryKind.Upsert);

		ScriptedVectorizer vectorizer = new();

		await Dispatcher(folder, outbox, vectorizer).DrainOnceAsync(CancellationToken.None);

		vectorizer.Upserts.Should().BeEmpty();
		outbox.StateOf(op).Should().Be(OpState.Done);
	}

	[Fact]
	public async Task An_Upsert_For_A_File_No_Longer_Recorded_Delivers_Nothing()
	{
		using TempFolder folder = new();
		InMemoryOutbox outbox = new();
		long op = outbox.Enqueue("removed.md", DeliveryKind.Upsert);

		ScriptedVectorizer vectorizer = new();

		await Dispatcher(folder, outbox, vectorizer).DrainOnceAsync(CancellationToken.None);

		vectorizer.Upserts.Should().BeEmpty();
		outbox.StateOf(op).Should().Be(OpState.Done);
	}

	/// <summary>
	/// The file is rewritten while its delivery is in progress, so the writers record new content. The
	/// delivery that just finished carried the old content, and must not be recorded as having delivered the
	/// new — otherwise the delivery queued for the new content would find it "already delivered" and skip.
	/// </summary>
	[Fact]
	public async Task A_File_Rewritten_During_Its_Delivery_Is_Not_Recorded_As_Delivered()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("note.md"), "before");

		InMemoryOutbox outbox = new();
		outbox.Record("note.md", "hash-before");
		long op = outbox.Enqueue("note.md", DeliveryKind.Upsert);

		ScriptedVectorizer vectorizer = new()
		{
			OnUpsert = (_, _) =>
			{
				outbox.Record("note.md", "hash-after");
				return Task.CompletedTask;
			},
		};

		await Dispatcher(folder, outbox, vectorizer).DrainOnceAsync(CancellationToken.None);

		outbox.LastSyncedHash("note.md").Should().BeNull("the content now recorded was never delivered");
		outbox.StateOf(op).Should().Be(OpState.Done);
	}

	/// <summary>
	/// An upsert and a delete for one file, run together or out of order, can leave content embedded for a
	/// file that no longer exists. With room to run four at once, one file's operations still run one at a
	/// time, in queue order.
	/// </summary>
	[Fact]
	public async Task One_Files_Operations_Run_In_Queue_Order_And_Never_Overlap()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("note.md"), "content");

		InMemoryOutbox outbox = new();
		outbox.Record("note.md", "hash-1");
		outbox.Enqueue("note.md", DeliveryKind.Upsert);
		outbox.Enqueue("note.md", DeliveryKind.Delete);
		outbox.Enqueue("note.md", DeliveryKind.Upsert);
		outbox.Enqueue("note.md", DeliveryKind.Delete);

		// Each delete stands for the file being rewritten, so the upsert after it has new content to deliver
		// rather than being skipped as already delivered — which is a different rule, not this one.
		int version = 1;
		ConcurrencyProbe probe = new();
		ScriptedVectorizer vectorizer = new()
		{
			OnUpsert = (_, _) => probe.Run("upsert"),
			OnDelete = async _ =>
			{
				await probe.Run("delete");
				outbox.Record("note.md", $"hash-{++version}");
			},
		};

		await Dispatcher(folder, outbox, vectorizer, new OutboxDispatcherOptions { Parallelism = 4 })
			.DrainOnceAsync(CancellationToken.None);

		probe.MaxConcurrent.Should().Be(1);
		probe.Order.Should().Equal("upsert", "delete", "upsert", "delete");
	}

	/// <summary>
	/// Each delivery waits until the other file's has started, so this completes only if the two really run
	/// at once. Run one at a time, the first would wait out its deadline and fail.
	/// </summary>
	[Fact]
	public async Task Different_Files_Are_Delivered_Concurrently()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("a.md"), "a");
		await File.WriteAllTextAsync(folder.Combine("b.md"), "b");

		InMemoryOutbox outbox = new();
		outbox.Record("a.md", "hash-a");
		outbox.Record("b.md", "hash-b");
		outbox.Enqueue("a.md", DeliveryKind.Upsert);
		outbox.Enqueue("b.md", DeliveryKind.Upsert);

		TaskCompletionSource aStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource bStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

		ScriptedVectorizer vectorizer = new()
		{
			OnUpsert = async (docId, token) =>
			{
				bool isA = docId == FileIdentity.For("a.md");
				(isA ? aStarted : bStarted).SetResult();
				await (isA ? bStarted : aStarted).Task.WaitAsync(TimeSpan.FromSeconds(10), token);
			},
		};

		await Dispatcher(folder, outbox, vectorizer, new OutboxDispatcherOptions { Parallelism = 2 })
			.DrainOnceAsync(CancellationToken.None);

		outbox.LastSyncedHash("a.md").Should().Be("hash-a");
		outbox.LastSyncedHash("b.md").Should().Be("hash-b");
	}

	[Fact]
	public async Task A_Failed_Delivery_Waits_Before_It_Is_Tried_Again_And_Waits_Longer_Each_Time()
	{
		using TempFolder folder = new();
		InMemoryOutbox outbox = new();
		long op = outbox.Enqueue("note.md", DeliveryKind.Delete);

		ManualClock clock = new();
		ScriptedVectorizer vectorizer = new() { OnDelete = _ => throw new IOException("backend unavailable") };
		OutboxDispatcher dispatcher = Dispatcher(folder, outbox, vectorizer, time: clock);

		await dispatcher.DrainOnceAsync(CancellationToken.None);

		outbox.DueAt(op).Should().Be(clock.GetUtcNow() + TimeSpan.FromSeconds(1));
		(await dispatcher.DrainOnceAsync(CancellationToken.None)).Should().Be(0, "it is not due yet");

		clock.Advance(TimeSpan.FromSeconds(1));
		await dispatcher.DrainOnceAsync(CancellationToken.None);

		outbox.AttemptsOf(op).Should().Be(2);
		outbox.DueAt(op).Should().Be(clock.GetUtcNow() + TimeSpan.FromSeconds(2));
	}

	[Fact]
	public void The_Retry_Delay_Doubles_And_Stops_At_Its_Cap()
	{
		OutboxDispatcher dispatcher = new(
			Path.GetTempPath(),
			new InMemoryOutbox(),
			new ScriptedVectorizer(),
			new OutboxDispatcherOptions { BaseRetryDelay = TimeSpan.FromSeconds(1), MaxRetryDelay = TimeSpan.FromSeconds(10) });

		Enumerable.Range(1, 6).Select(dispatcher.RetryDelay).Should().Equal(
			TimeSpan.FromSeconds(1),
			TimeSpan.FromSeconds(2),
			TimeSpan.FromSeconds(4),
			TimeSpan.FromSeconds(8),
			TimeSpan.FromSeconds(10),
			TimeSpan.FromSeconds(10));
	}

	[Fact]
	public async Task A_Delivery_That_Recovers_Is_Recorded_Delivered()
	{
		using TempFolder folder = new();
		await File.WriteAllTextAsync(folder.Combine("note.md"), "content");

		InMemoryOutbox outbox = new();
		outbox.Record("note.md", "hash-1");
		long op = outbox.Enqueue("note.md", DeliveryKind.Upsert);

		ManualClock clock = new();
		int calls = 0;
		ScriptedVectorizer vectorizer = new()
		{
			OnUpsert = (_, _) => ++calls == 1 ? throw new IOException("transient") : Task.CompletedTask,
		};
		OutboxDispatcher dispatcher = Dispatcher(folder, outbox, vectorizer, time: clock);

		await dispatcher.DrainOnceAsync(CancellationToken.None);
		clock.Advance(TimeSpan.FromMinutes(1));
		await dispatcher.DrainOnceAsync(CancellationToken.None);

		outbox.StateOf(op).Should().Be(OpState.Done);
		outbox.LastSyncedHash("note.md").Should().Be("hash-1");
	}

	/// <summary>
	/// Retired as done, an abandoned delivery would be indistinguishable from one that arrived. And the error
	/// kept must be the one that ended it — each attempt here fails differently, so keeping an earlier one
	/// shows.
	/// </summary>
	[Fact]
	public async Task A_Delivery_That_Keeps_Failing_Is_Retired_As_Failed_With_The_Error_That_Ended_It()
	{
		using TempFolder folder = new();
		InMemoryOutbox outbox = new();
		long op = outbox.Enqueue("note.md", DeliveryKind.Delete);

		ManualClock clock = new();
		int calls = 0;
		ScriptedVectorizer vectorizer = new() { OnDelete = _ => throw new IOException($"attempt {++calls}") };
		OutboxDispatcher dispatcher = Dispatcher(folder, outbox, vectorizer, new OutboxDispatcherOptions { MaxAttempts = 3 }, clock);

		for (int i = 0; i < 5; i++)
		{
			await dispatcher.DrainOnceAsync(CancellationToken.None);
			clock.Advance(TimeSpan.FromHours(1));
		}

		calls.Should().Be(3);
		outbox.StateOf(op).Should().Be(OpState.Failed);
		outbox.AttemptsOf(op).Should().Be(3);
		outbox.ErrorOf(op).Should().Be("IOException: attempt 3");
	}

	[Fact]
	public async Task The_Run_Requeues_Operations_Left_In_Flight_By_A_Previous_Process()
	{
		using TempFolder folder = new();
		InMemoryOutbox outbox = new();
		long op = outbox.Enqueue("note.md", DeliveryKind.Delete, inFlight: true);

		ScriptedVectorizer vectorizer = new();

		await RunUntil(Dispatcher(folder, outbox, vectorizer, Fast), () => outbox.StateOf(op) == OpState.Done);

		vectorizer.Deletes.Should().ContainSingle();
	}

	/// <summary>
	/// Every queued file depends on this loop. If one fault could end it, the outbox would look empty for the
	/// rest of the process's life.
	/// </summary>
	[Fact]
	public async Task A_Failed_Drain_Does_Not_End_The_Run()
	{
		using TempFolder folder = new();
		InMemoryOutbox outbox = new() { FailClaims = 3 };
		long op = outbox.Enqueue("note.md", DeliveryKind.Delete);

		await RunUntil(Dispatcher(folder, outbox, new ScriptedVectorizer(), Fast), () => outbox.StateOf(op) == OpState.Done);

		outbox.ClaimCalls.Should().BeGreaterThan(3);
	}

	/// <summary>
	/// Five deliveries, one per drain, then silence. One checkpoint, when the burst ends — not one per
	/// operation, and not one per idle poll afterwards.
	/// </summary>
	[Fact]
	public async Task A_Burst_Checkpoints_Once_When_It_Ends_Not_Per_Operation_And_Not_While_Idle()
	{
		using TempFolder folder = new();
		InMemoryOutbox outbox = new();
		long[] ops = [.. Enumerable.Range(0, 5).Select(i => outbox.Enqueue($"file{i}.md", DeliveryKind.Delete))];

		await RunWhile(Dispatcher(folder, outbox, new ScriptedVectorizer(), Fast with { BatchSize = 1 }), async () =>
		{
			await Until(() => ops.All(op => outbox.StateOf(op) == OpState.Done) && outbox.CheckpointCalls > 0);

			// Twenty idle polls' worth of nothing to do.
			await Task.Delay(200);
		});

		outbox.CheckpointCalls.Should().Be(1);
	}

	[Fact]
	public async Task A_Dispatcher_That_Never_Delivered_Never_Checkpoints()
	{
		using TempFolder folder = new();
		InMemoryOutbox outbox = new();

		await RunWhile(Dispatcher(folder, outbox, new ScriptedVectorizer(), Fast), () => Task.Delay(200));

		outbox.ClaimCalls.Should().BeGreaterThan(1, "it polled");
		outbox.CheckpointCalls.Should().Be(0);
	}

	[Fact]
	public async Task Each_Burst_Checkpoints_When_It_Ends()
	{
		using TempFolder folder = new();
		InMemoryOutbox outbox = new();
		outbox.Enqueue("first.md", DeliveryKind.Delete);

		await RunWhile(Dispatcher(folder, outbox, new ScriptedVectorizer(), Fast), async () =>
		{
			await Until(() => outbox.CheckpointCalls == 1);

			outbox.Enqueue("second.md", DeliveryKind.Delete);
			await Until(() => outbox.CheckpointCalls == 2);
		});

		outbox.CheckpointCalls.Should().Be(2);
	}

	/// <summary>
	/// A drain that failed found nothing out about whether the burst is over, so it is not a quiet moment.
	/// The store goes away during a delivery; no checkpoint while it is gone, one once it is back and the
	/// outbox is seen to be empty.
	/// </summary>
	[Fact]
	public async Task A_Failed_Drain_Is_Not_Taken_For_The_End_Of_A_Burst()
	{
		using TempFolder folder = new();
		InMemoryOutbox outbox = new();
		long op = outbox.Enqueue("note.md", DeliveryKind.Delete);

		ScriptedVectorizer vectorizer = new()
		{
			OnDelete = _ =>
			{
				outbox.ClaimsFail = true;
				return Task.CompletedTask;
			},
		};

		await RunWhile(Dispatcher(folder, outbox, vectorizer, Fast), async () =>
		{
			await Until(() => outbox.StateOf(op) == OpState.Done);
			int claimsBefore = outbox.ClaimCalls;
			await Until(() => outbox.ClaimCalls > claimsBefore + 5);

			outbox.CheckpointCalls.Should().Be(0, "every drain since the delivery failed");

			outbox.ClaimsFail = false;
			await Until(() => outbox.CheckpointCalls == 1);
		});
	}

	/// <summary>A checkpoint reclaims disk and delivers nothing; one that fails must not stop deliveries.</summary>
	[Fact]
	public async Task A_Failing_Checkpoint_Does_Not_Stop_Deliveries()
	{
		using TempFolder folder = new();
		InMemoryOutbox outbox = new() { CheckpointsFail = true };
		outbox.Enqueue("first.md", DeliveryKind.Delete);

		ScriptedVectorizer vectorizer = new();

		await RunWhile(Dispatcher(folder, outbox, vectorizer, Fast), async () =>
		{
			await Until(() => outbox.CheckpointCalls == 1);

			long later = outbox.Enqueue("second.md", DeliveryKind.Delete);
			await Until(() => outbox.StateOf(later) == OpState.Done);
		});

		vectorizer.Deletes.Should().HaveCount(2);
	}

	[Fact]
	public async Task Cancelling_Ends_The_Run()
	{
		using TempFolder folder = new();
		using CancellationTokenSource stopping = new();

		Task run = Dispatcher(folder, new InMemoryOutbox(), new ScriptedVectorizer(), Fast).RunAsync(stopping.Token);
		await Task.Delay(50);
		await stopping.CancelAsync();

		Func<Task> stop = async () => await run;

		await stop.Should().CompleteWithinAsync(TimeSpan.FromSeconds(20));
	}

	// ── what the loop survives, and what it gives up on ───────────────────────

	/// <summary>
	/// The one error this library raises, and the level is the point. The operation is retired and the
	/// file marked failed, so the stored state is durable and correct — and completely silent. Nothing
	/// will try it again, and its only other symptom is a search that does not find a file that is
	/// plainly there. The attempts before it are debug: work that recovers on its own should not page
	/// anybody, and an embedding backend restarting produces a run of them.
	/// </summary>
	[Fact]
	public async Task A_Delivery_Retired_As_Failed_Is_Recorded_At_Error_And_Its_Retries_At_Debug()
	{
		using TempFolder folder = new();
		InMemoryOutbox outbox = new();
		outbox.Enqueue("note.md", DeliveryKind.Delete);

		ManualClock clock = new();
		ScriptedVectorizer vectorizer = new() { OnDelete = _ => throw new IOException("no backend there") };
		CapturingLogger<OutboxDispatcher> log = new();

		OutboxDispatcher dispatcher = Dispatcher(
			folder, outbox, vectorizer, new OutboxDispatcherOptions { MaxAttempts = 3 }, clock, log);

		for (int i = 0; i < 4; i++)
		{
			await dispatcher.DrainOnceAsync(CancellationToken.None);
			clock.Advance(TimeSpan.FromHours(1));
		}

		log.At(LogLevel.Error).Should().ContainSingle()
			.Which.Message.Should().Contain("note.md");

		log.At(LogLevel.Debug).Should().HaveCount(2, "the attempts that were still going to be tried again");
	}

	/// <summary>
	/// Every queued file depends on this loop continuing, so a drain that fails on every pass is
	/// indistinguishable from an outbox with nothing in it — the same shape as the reconciler's.
	/// </summary>
	[Fact]
	public async Task A_Failed_Drain_Is_Recorded_So_A_Loop_That_Delivers_Nothing_Is_Not_Silent()
	{
		using TempFolder folder = new();
		InMemoryOutbox outbox = new() { FailClaims = 2 };
		long op = outbox.Enqueue("note.md", DeliveryKind.Delete);

		CapturingLogger<OutboxDispatcher> log = new();

		await RunUntil(
			Dispatcher(folder, outbox, new ScriptedVectorizer(), Fast, logger: log),
			() => outbox.StateOf(op) == OpState.Done);

		log.At(LogLevel.Warning).Should().HaveCount(2, "one line for each drain that failed");
	}

	/// <summary>
	/// A checkpoint reclaims disk and delivers nothing, so one that could not be taken is debug rather
	/// than a fault — but it is recorded, because a write-ahead log quietly growing has no other symptom.
	/// </summary>
	[Fact]
	public async Task A_Checkpoint_That_Could_Not_Be_Taken_Is_Recorded_At_Debug_And_Nothing_Louder()
	{
		using TempFolder folder = new();
		InMemoryOutbox outbox = new() { CheckpointsFail = true };
		outbox.Enqueue("first.md", DeliveryKind.Delete);

		CapturingLogger<OutboxDispatcher> log = new();

		await RunWhile(
			Dispatcher(folder, outbox, new ScriptedVectorizer(), Fast, logger: log),
			() => Until(() => outbox.CheckpointCalls == 1));

		log.At(LogLevel.Debug).Should().NotBeEmpty();
		log.Lines.Should().OnlyContain(
			line => line.Level == LogLevel.Debug,
			"nothing that was queued failed to arrive; only the disk reclaim did");
	}

	private static readonly OutboxDispatcherOptions Fast = new() { PollInterval = TimeSpan.FromMilliseconds(10) };

	private static OutboxDispatcher Dispatcher(
		TempFolder folder,
		IOutboxStore outbox,
		IVectorizationService vectorizer,
		OutboxDispatcherOptions? options = null,
		TimeProvider? time = null,
		ILogger<OutboxDispatcher>? logger = null)
		=> new(folder.Path, outbox, vectorizer, options, time, logger);

	/// <summary>Runs the dispatcher for the duration of <paramref name="scenario"/>, then stops it.</summary>
	private static async Task RunWhile(OutboxDispatcher dispatcher, Func<Task> scenario)
	{
		using CancellationTokenSource stopping = new();
		Task run = dispatcher.RunAsync(stopping.Token);

		try
		{
			await scenario();
		}
		finally
		{
			await stopping.CancelAsync();
			await run;
		}
	}

	/// <summary>Waits for <paramref name="condition"/>, failing if it has not held within a generous deadline.</summary>
	private static async Task Until(Func<bool> condition)
	{
		DateTime deadline = DateTime.UtcNow.AddSeconds(20);

		while (!condition() && DateTime.UtcNow < deadline)
		{
			await Task.Delay(10);
		}

		condition().Should().BeTrue("it should have happened well within the deadline");
	}

	private static async Task RunUntil(OutboxDispatcher dispatcher, Func<bool> condition)
	{
		using CancellationTokenSource stopping = new();
		Task run = dispatcher.RunAsync(stopping.Token);

		DateTime deadline = DateTime.UtcNow.AddSeconds(20);

		while (!condition() && DateTime.UtcNow < deadline)
		{
			await Task.Delay(10);
		}

		await stopping.CancelAsync();
		await run;

		condition().Should().BeTrue("the run should have got there well within its deadline");
	}

	internal enum OpState
	{
		Pending,
		InFlight,
		Done,
		Failed,
	}

	/// <summary>An outbox in memory, following the store contract, locked throughout for parallel delivery.</summary>
	private sealed class InMemoryOutbox : IOutboxStore
	{
		private readonly Lock _gate = new();
		private readonly Dictionary<string, DeliveryRecord> _files = new(StringComparer.Ordinal);
		private readonly List<Entry> _ops = [];
		private long _nextId = 1;

		public int FailClaims { get; init; }

		public int ClaimCalls { get; private set; }

		/// <summary>While set, every claim fails — for a store that goes away part-way through a run.</summary>
		public bool ClaimsFail { get; set; }

		public bool CheckpointsFail { get; init; }

		public int CheckpointCalls => Volatile.Read(ref _checkpointCalls);

		private int _checkpointCalls;

		public Task CheckpointAsync(CancellationToken cancellationToken)
		{
			Interlocked.Increment(ref _checkpointCalls);

			return CheckpointsFail
				? Task.FromException(new IOException("database is locked"))
				: Task.CompletedTask;
		}

		public void Record(string relativePath, string contentHash, long size = 0, DateTime createdUtc = default, string? syncedHash = null)
		{
			lock (_gate)
			{
				string? kept = syncedHash ?? _files.GetValueOrDefault(relativePath)?.LastSyncedHash;
				_files[relativePath] = new DeliveryRecord(new FileRecord(relativePath, contentHash, size, createdUtc), kept);
			}
		}

		public long Enqueue(string relativePath, DeliveryKind kind, bool inFlight = false)
		{
			lock (_gate)
			{
				Entry entry = new(_nextId++, relativePath, kind)
				{
					State = inFlight ? OpState.InFlight : OpState.Pending,
				};
				_ops.Add(entry);

				return entry.Id;
			}
		}

		public string? LastSyncedHash(string relativePath)
		{
			lock (_gate)
			{
				return _files[relativePath].LastSyncedHash;
			}
		}

		public OpState StateOf(long id) => Find(id).State;

		public int AttemptsOf(long id) => Find(id).Attempts;

		public DateTimeOffset DueAt(long id) => Find(id).Due;

		public string? ErrorOf(long id) => Find(id).Error;

		public Task RequeueInFlightAsync(CancellationToken cancellationToken)
		{
			lock (_gate)
			{
				foreach (Entry entry in _ops.Where(e => e.State == OpState.InFlight))
				{
					entry.State = OpState.Pending;
				}
			}

			return Task.CompletedTask;
		}

		public Task<IReadOnlyList<OutboxOp>> ClaimDueAsync(int max, DateTimeOffset now, CancellationToken cancellationToken)
		{
			lock (_gate)
			{
				ClaimCalls++;

				if (ClaimCalls <= FailClaims || ClaimsFail)
				{
					throw new InvalidOperationException("the store is unavailable");
				}

				List<Entry> due = [.. _ops.Where(e => e.State == OpState.Pending && e.Due <= now).OrderBy(e => e.Id).Take(max)];

				foreach (Entry entry in due)
				{
					entry.State = OpState.InFlight;
				}

				return Task.FromResult<IReadOnlyList<OutboxOp>>(
					[.. due.Select(e => new OutboxOp(e.Id, e.RelativePath, e.Kind, e.Attempts))]);
			}
		}

		public Task<DeliveryRecord?> ReadForDeliveryAsync(string relativePath, CancellationToken cancellationToken)
		{
			lock (_gate)
			{
				return Task.FromResult(_files.GetValueOrDefault(relativePath));
			}
		}

		public Task<bool> TryMarkSyncedAsync(string relativePath, string expectedContentHash, CancellationToken cancellationToken)
		{
			lock (_gate)
			{
				if (!_files.TryGetValue(relativePath, out DeliveryRecord? record) || record.File.ContentHash != expectedContentHash)
				{
					return Task.FromResult(false);
				}

				_files[relativePath] = record with { LastSyncedHash = expectedContentHash };

				return Task.FromResult(true);
			}
		}

		public Task MarkDoneAsync(long opId, CancellationToken cancellationToken)
			=> Update(opId, e => e.State = OpState.Done);

		public Task RescheduleAsync(long opId, int attempts, DateTimeOffset nextAttemptUtc, string error, CancellationToken cancellationToken)
			=> Update(opId, e =>
			{
				e.State = OpState.Pending;
				e.Attempts = attempts;
				e.Due = nextAttemptUtc;
				e.Error = error;
			});

		public Task MarkAbandonedAsync(long opId, int attempts, string error, CancellationToken cancellationToken)
			=> Update(opId, e =>
			{
				e.State = OpState.Failed;
				e.Attempts = attempts;
				e.Error = error;
			});

		private Entry Find(long id)
		{
			lock (_gate)
			{
				return _ops.Single(e => e.Id == id);
			}
		}

		private Task Update(long id, Action<Entry> change)
		{
			lock (_gate)
			{
				change(_ops.Single(e => e.Id == id));
			}

			return Task.CompletedTask;
		}

		private sealed class Entry(long id, string relativePath, DeliveryKind kind)
		{
			public long Id { get; } = id;

			public string RelativePath { get; } = relativePath;

			public DeliveryKind Kind { get; } = kind;

			public OpState State { get; set; }

			public int Attempts { get; set; }

			public DateTimeOffset Due { get; set; } = DateTimeOffset.MinValue;

			public string? Error { get; set; }
		}
	}

	private sealed record Delivery(string DocId, string Content, FileMetadata Metadata);

	private sealed class ScriptedVectorizer : IVectorizationService
	{
		public Func<string, CancellationToken, Task> OnUpsert { get; init; } = (_, _) => Task.CompletedTask;

		public Func<string, Task> OnDelete { get; init; } = _ => Task.CompletedTask;

		public ConcurrentQueue<Delivery> Upserts { get; } = new();

		public ConcurrentQueue<string> Deletes { get; } = new();

		public async Task UpsertAsync(string docId, Stream content, FileMetadata metadata, CancellationToken cancellationToken)
		{
			using StreamReader reader = new(content);
			string text = await reader.ReadToEndAsync(cancellationToken);

			await OnUpsert(docId, cancellationToken);
			Upserts.Enqueue(new Delivery(docId, text, metadata));
		}

		public async Task DeleteAsync(string docId, CancellationToken cancellationToken)
		{
			await OnDelete(docId);
			Deletes.Enqueue(docId);
		}
	}

	/// <summary>Records how many calls overlap, and the order they started in.</summary>
	private sealed class ConcurrencyProbe
	{
		private int _current;
		private int _max;

		public ConcurrentQueue<string> Order { get; } = new();

		public int MaxConcurrent => _max;

		public async Task Run(string name)
		{
			Order.Enqueue(name);
			int now = Interlocked.Increment(ref _current);

			int seen;
			do
			{
				seen = _max;
			}
			while (now > seen && Interlocked.CompareExchange(ref _max, now, seen) != seen);

			// Long enough that overlapping calls would actually overlap.
			await Task.Delay(50);
			Interlocked.Decrement(ref _current);
		}
	}

	private sealed class ManualClock : TimeProvider
	{
		private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

		public override DateTimeOffset GetUtcNow() => _now;

		public void Advance(TimeSpan by) => _now += by;
	}
}
