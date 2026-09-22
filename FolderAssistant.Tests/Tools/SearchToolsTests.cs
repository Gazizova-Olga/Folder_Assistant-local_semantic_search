using FluentAssertions;
using FolderAssistant.Retrieval;
using FolderAssistant.Tools;

namespace FolderAssistant.Tests.Tools;

/// <summary>
/// The file-level semantic search (SPEC-101), asserted on its own arithmetic over a query that answers
/// with fixed hits: how passages fold into files, how files rank, what the bounds cut, and that the
/// refusal of a not-ready index passes straight through.
/// </summary>
public sealed class SearchToolsTests
{
	private const String Database = "db";

	[Fact]
	public void Passages_Fold_Into_Files_Ranked_By_Their_Best_Passage()
	{
		// a.md's best passage is deliberately not its first: a file is as relevant as its best passage,
		// wherever that passage sits in the hits.
		FixedQuery query = new(
			Hit("c1", "docs/a.md", 0.80),
			Hit("c2", "docs/b.md", 0.95),
			Hit("c3", "docs/a.md", 0.90),
			Hit("c4", "docs/a.md", 0.10),
			Hit("c5", "notes.txt", 0.50));

		FilesAbout result = new SearchTools(query, Database).FindFilesAbout("anything");

		result.Query.Should().Be("anything");
		result.Files.Select(file => (file.Path, file.Score, file.MatchingChunks)).Should().Equal(
			(Path.Combine("docs", "b.md"), 0.95, 1),
			(Path.Combine("docs", "a.md"), 0.90, 3),
			("notes.txt", 0.50, 1));
		result.Truncated.Should().BeFalse();
		result.Note.Should().BeNull();
	}

	[Fact]
	public void Files_With_Equal_Scores_Are_Ordered_By_Path()
	{
		FixedQuery query = new(Hit("c1", "z.md", 0.5), Hit("c2", "a.md", 0.5), Hit("c3", "m.md", 0.5));

		FilesAbout result = new SearchTools(query, Database).FindFilesAbout("tie");

		result.Files.Select(file => file.Path).Should().Equal("a.md", "m.md", "z.md");
	}

	[Fact]
	public void The_Query_Goes_Through_The_Composed_Seam_With_An_Over_Fetched_K()
	{
		FixedQuery query = new();

		new SearchTools(query, Database).FindFilesAbout("topic", maxFiles: 4);

		query.Calls.Should().ContainSingle();
		query.Calls[0].DatabasePath.Should().Be(Database);
		query.Calls[0].QueryText.Should().Be("topic");
		query.Calls[0].Options.TopK.Should().Be(4 * SearchTools.CandidatesPerFile);
	}

	[Fact]
	public void The_Candidate_Pool_Is_Capped_And_MaxFiles_Is_Cut_With_A_Note()
	{
		FixedQuery query = new();

		FilesAbout result = new SearchTools(query, Database).FindFilesAbout("topic", maxFiles: SearchTools.MaxFiles + 30);

		query.Calls[0].Options.TopK.Should().Be(SearchTools.MaxCandidates);
		result.Note.Should().Contain($"maxFiles cut to {SearchTools.MaxFiles}");
	}

	[Fact]
	public void More_Files_Than_Asked_For_Are_Cut_And_Said()
	{
		FixedQuery query = new(Enumerable.Range(0, 7).Select(i => Hit($"c{i}", $"f{i}.md", 1.0 - i * 0.1)).ToArray());

		FilesAbout result = new SearchTools(query, Database).FindFilesAbout("topic", maxFiles: 3);

		result.Files.Select(file => file.Path).Should().Equal("f0.md", "f1.md", "f2.md");
		result.Truncated.Should().BeTrue();
		result.Note.Should().Contain("cut at 3 files").And.Contain("spanned 7");
	}

	[Fact]
	public void A_Full_Candidate_Pool_Is_Said_Even_When_Nothing_Was_Cut()
	{
		// Two files asked for, ten passages fetched, all ten from one file: nothing is cut, but a file
		// whose passages all rank eleventh or lower would not appear, and the note says so.
		FixedQuery query = new(Enumerable.Range(0, 2 * SearchTools.CandidatesPerFile).Select(i => Hit($"c{i}", "one.md", 0.9)).ToArray());

		FilesAbout result = new SearchTools(query, Database).FindFilesAbout("topic", maxFiles: 2);

		result.Files.Should().ContainSingle();
		result.Truncated.Should().BeFalse();
		result.Note.Should().Contain("a file whose passages all rank lower is not listed");
	}

	[Fact]
	public void No_Hits_Is_An_Empty_Result_With_A_Note_Not_An_Error()
	{
		FilesAbout result = new SearchTools(new FixedQuery(), Database).FindFilesAbout("nothing here");

		result.Files.Should().BeEmpty();
		result.Truncated.Should().BeFalse();
		result.Note.Should().Contain("no indexed passage ranked");
	}

	[Fact]
	public void A_Not_Ready_Index_Refuses_Through_The_Tool_Unchanged()
	{
		ThrowingQuery query = new(new IndexNotReadyException("still building"));

		Action act = () => new SearchTools(query, Database).FindFilesAbout("topic");

		act.Should().Throw<IndexNotReadyException>().WithMessage("still building");
	}

	[Fact]
	public void An_Empty_Query_And_A_Zero_Count_Throw()
	{
		SearchTools tools = new(new FixedQuery(), Database);

		Action empty = () => tools.FindFilesAbout("   ");
		Action zero = () => tools.FindFilesAbout("topic", maxFiles: 0);

		empty.Should().Throw<ArgumentException>();
		zero.Should().Throw<ArgumentOutOfRangeException>();
	}

	private static RetrievalHit Hit(String chunkId, String path, Double score)
		=> new(chunkId, path, 0, 0, 10, score, "hash");

	private sealed record Call(String DatabasePath, String QueryText, RetrievalOptions Options);

	private sealed class FixedQuery(params RetrievalHit[] hits) : IRetrievalQuery
	{
		public List<Call> Calls { get; } = [];

		public IReadOnlyList<RetrievalHit> Search(String databasePath, String queryText, RetrievalOptions options)
		{
			this.Calls.Add(new Call(databasePath, queryText, options));

			return hits.Take(options.TopK).ToArray();
		}
	}

	private sealed class ThrowingQuery(Exception exception) : IRetrievalQuery
	{
		public IReadOnlyList<RetrievalHit> Search(String databasePath, String queryText, RetrievalOptions options)
			=> throw exception;
	}
}
