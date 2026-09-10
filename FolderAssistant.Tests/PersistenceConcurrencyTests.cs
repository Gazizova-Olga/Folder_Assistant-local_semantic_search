using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using FolderAssistant.Embedding;
using FolderAssistant.Indexing;
using FolderAssistant.Persistence;
using FolderAssistant.Retrieval;

namespace FolderAssistant.Tests;

/// <summary>
/// One writer, many readers, against one database file — the steady state of the running
/// application, and the thing the connection settings exist for.
///
/// <para>
/// Both tests repeat rather than running once, and that is not padding. The fault a shared cache
/// reintroduces is probabilistic: a single round of concurrent callers passes comfortably with the
/// bug present, which is exactly how such a bug survives in a suite that is otherwise green.
/// Repetition is what turns "usually passes" into something that actually fails.
/// </para>
/// </summary>
public sealed class PersistenceConcurrencyTests
{
	private static readonly IndexingConfig Config = new() { ChunkSizeTokens = 8, ChunkOverlapTokens = 2 };

	[Fact]
	[SuppressMessage("Major Code Smell", "S1215:GC.Collect should not be called",
		Justification = "The collect is what makes this test detect anything: the fault it guards against is a " +
			"database handle finalized while another connection is inside sqlite3_prepare_v2, so a collection has " +
			"to land during the concurrent work. Remove it and the same iterations pass with the shared cache " +
			"restored — the test silently stops guarding.")]
	[SuppressMessage("Usage", "xUnit1031:Do not use blocking task operations in test method",
		Justification = "Deliberate: the test drives eight threads at one bootstrap and joins them. That concurrent " +
			"join is the scenario under test, not an accident.")]
	public void Concurrent_Bootstraps_Of_The_Same_Folder_Never_Fault()
	{
		List<Exception> failures = [];

		// Every part of this shape is load-bearing, so please do not tidy it:
		//
		//   - The repetition, for the reason in the class comment.
		//   - The explicit GC.Collect(). The fault is a shared-cache database handle being finalized
		//     while another connection is inside sqlite3_prepare_v2, so collections have to land
		//     *during* the concurrent work. Without this line the same 60 iterations pass even with
		//     the shared cache restored, and the test quietly stops guarding anything.
		for (Int32 iteration = 0; iteration < 60; iteration++)
		{
			using TempFolder folder = new();
			PersistenceConfig persistence = new();

			Task<DatabaseBootstrapResult>[] bootstraps = Enumerable.Range(0, 8)
				.Select(_ => Task.Run(() => new FolderDatabaseBootstrapper().EnsureInitialized(folder.Path, persistence)))
				.ToArray();

			try
			{
				Task.WaitAll(bootstraps);
			}
			catch (AggregateException ex)
			{
				failures.AddRange(ex.Flatten().InnerExceptions);
			}

			GC.Collect();
		}

		// Restore the shared cache on the connection string and this faults inside sqlite3_prepare_v2.
		failures.Should().BeEmpty();
	}

	/// <summary>
	/// What the application actually does: the indexing service rewriting the index on a background
	/// thread while retrieval answers on request threads. WAL is what makes it legal; a shared cache
	/// would put both behind the same locks.
	/// </summary>
	[Fact]
	public async Task Retrieval_Can_Read_While_The_Indexer_Writes()
	{
		using TempFolder folder = new();

		for (Int32 i = 0; i < 10; i++)
		{
			await File.WriteAllTextAsync(folder.Combine($"doc{i}.md"), $"alpha beta gamma delta document number {i}");
		}

		String databasePath = new FolderDatabaseBootstrapper()
			.EnsureInitialized(folder.Path, new PersistenceConfig())
			.DatabasePath;

		ProgrammableEmbeddingVectorizer vectorizer = new("concurrency-v1", 32);
		FolderIndexingPipeline pipeline = new(vectorizer);

		// Seed the index, so the readers have something to find rather than racing an empty table.
		pipeline.Run(folder.Path, databasePath, Config);

		using CancellationTokenSource stop = new(TimeSpan.FromSeconds(5));
		List<Exception> writerFailures = [];
		List<Exception> readerFailures = [];

		Task writer = Task.Run(() =>
		{
			while (!stop.IsCancellationRequested)
			{
				try
				{
					File.WriteAllText(folder.Combine("doc0.md"), $"alpha beta gamma delta rewritten {Guid.NewGuid():N}");
					pipeline.Run(folder.Path, databasePath, Config);
				}
				catch (Exception ex)
				{
					writerFailures.Add(ex);
					return;
				}
			}
		});

		Task[] readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
		{
			CosineRetrievalQuery retrieval = new(vectorizer);

			while (!stop.IsCancellationRequested)
			{
				try
				{
					retrieval.Search(databasePath, "alpha beta", new RetrievalOptions(TopK: 3));
				}
				catch (Exception ex)
				{
					readerFailures.Add(ex);
					return;
				}
			}
		})).ToArray();

		await Task.WhenAll([.. readers, writer]);

		writerFailures.Should().BeEmpty();
		readerFailures.Should().BeEmpty();
	}
}
