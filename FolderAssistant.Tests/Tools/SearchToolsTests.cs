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

	private static readonly IContextReduction Reducer = new TokenBudgetContextReducer();

	/// <summary>
	/// A folder holding every file the hits name, and a builder over it. The file-level search reads no
	/// passage, but it does ask whether a ranked file is still there, so a fixture whose files do not
	/// exist would have every result correctly thrown away.
	/// </summary>
	private static PassageBuilder BuilderOver(TempFolder root, params String[] relativePaths)
	{
		foreach (String relativePath in relativePaths)
		{
			String full = root.Combine(relativePath.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(full)!);
			File.WriteAllText(full, "ranked");
		}

		return new PassageBuilder(root.Path, FolderAssistant.Extraction.TextExtractorRegistry.Default);
	}

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

		using TempFolder root = new();
		FilesAbout result = new SearchTools(query, Database, BuilderOver(root, "docs/a.md", "docs/b.md", "notes.txt"), Reducer)
			.FindFilesAbout("anything");

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

		using TempFolder root = new();
		FilesAbout result = new SearchTools(query, Database, BuilderOver(root, "z.md", "a.md", "m.md"), Reducer).FindFilesAbout("tie");

		result.Files.Select(file => file.Path).Should().Equal("a.md", "m.md", "z.md");
	}

	[Fact]
	public void The_Query_Goes_Through_The_Composed_Seam_With_An_Over_Fetched_K()
	{
		FixedQuery query = new();

		using TempFolder root = new();
		new SearchTools(query, Database, BuilderOver(root), Reducer).FindFilesAbout("topic", maxFiles: 4);

		query.Calls.Should().ContainSingle();
		query.Calls[0].DatabasePath.Should().Be(Database);
		query.Calls[0].QueryText.Should().Be("topic");
		query.Calls[0].Options.TopK.Should().Be(4 * SearchTools.CandidatesPerFile);
	}

	[Fact]
	public void The_Candidate_Pool_Is_Capped_And_MaxFiles_Is_Cut_With_A_Note()
	{
		FixedQuery query = new();

		using TempFolder root = new();
		FilesAbout result = new SearchTools(query, Database, BuilderOver(root), Reducer).FindFilesAbout("topic", maxFiles: SearchTools.MaxFiles + 30);

		query.Calls[0].Options.TopK.Should().Be(SearchTools.MaxCandidates);
		result.Note.Should().Contain($"maxFiles cut to {SearchTools.MaxFiles}");
	}

	[Fact]
	public void More_Files_Than_Asked_For_Are_Cut_And_Said()
	{
		FixedQuery query = new(Enumerable.Range(0, 7).Select(i => Hit($"c{i}", $"f{i}.md", 1.0 - i * 0.1)).ToArray());

		using TempFolder root = new();
		FilesAbout result = new SearchTools(query, Database, BuilderOver(root, [.. Enumerable.Range(0, 7).Select(i => $"f{i}.md")]), Reducer)
			.FindFilesAbout("topic", maxFiles: 3);

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

		using TempFolder root = new();
		FilesAbout result = new SearchTools(query, Database, BuilderOver(root, "one.md"), Reducer).FindFilesAbout("topic", maxFiles: 2);

		result.Files.Should().ContainSingle();
		result.Truncated.Should().BeFalse();
		result.Note.Should().Contain("a file whose passages all rank lower is not listed");
	}

	/// <summary>
	/// A deleted file keeps its chunks and vectors until its removal is delivered, so the index can still
	/// rank it. Naming it would be an assertion about the folder that is false — and this tool tells the
	/// caller to read what it names.
	/// </summary>
	[Fact]
	public void A_Ranked_File_The_Folder_No_Longer_Holds_Is_Not_Listed()
	{
		using TempFolder root = new();
		FixedQuery query = new(Hit("c1", "kept.md", 0.90), Hit("c2", "gone.md", 0.95), Hit("c3", "also-gone.md", 0.80));

		// Only kept.md is on disk; the other two are as the index remembers them.
		FilesAbout result = new SearchTools(query, Database, BuilderOver(root, "kept.md"), Reducer).FindFilesAbout("topic");

		result.Files.Select(file => file.Path).Should().Equal("kept.md");
		result.Note.Should().Contain("2 ranked files are no longer in the folder");
	}

	/// <summary>One gone file reads as one, not as "1 files".</summary>
	[Fact]
	public void One_Missing_File_Is_Said_In_The_Singular()
	{
		using TempFolder root = new();
		FixedQuery query = new(Hit("c1", "kept.md", 0.90), Hit("c2", "gone.md", 0.95));

		FilesAbout result = new SearchTools(query, Database, BuilderOver(root, "kept.md"), Reducer).FindFilesAbout("topic");

		result.Note.Should().Contain("1 ranked file is no longer in the folder");
	}

	/// <summary>
	/// The check asks only whether the file is there. A file edited since indexing keeps its score and is
	/// still listed, which is what answering from an index means — verifying that is SearchIndex's job,
	/// and it costs a read and a hash per passage.
	/// </summary>
	[Fact]
	public void A_File_Edited_Since_Indexing_Is_Still_Listed()
	{
		using TempFolder root = new();
		FixedQuery query = new(Hit("c1", "edited.md", 0.90));
		PassageBuilder passages = BuilderOver(root, "edited.md");
		File.WriteAllText(root.Combine("edited.md"), "something else entirely");

		FilesAbout result = new SearchTools(query, Database, passages, Reducer).FindFilesAbout("topic");

		result.Files.Select(file => file.Path).Should().Equal("edited.md");
		result.Note.Should().NotContain("no longer in the folder");
	}

	[Fact]
	public void No_Hits_Is_An_Empty_Result_With_A_Note_Not_An_Error()
	{
		using TempFolder root = new();
		FilesAbout result = new SearchTools(new FixedQuery(), Database, BuilderOver(root), Reducer).FindFilesAbout("nothing here");

		result.Files.Should().BeEmpty();
		result.Truncated.Should().BeFalse();
		result.Note.Should().Contain("no indexed passage ranked");
	}

	[Fact]
	public void A_Not_Ready_Index_Refuses_Through_The_Tool_Unchanged()
	{
		ThrowingQuery query = new(new IndexNotReadyException("still building"));

		using TempFolder root = new();
		Action act = () => new SearchTools(query, Database, BuilderOver(root), Reducer).FindFilesAbout("topic");

		act.Should().Throw<IndexNotReadyException>().WithMessage("still building");
	}

	[Fact]
	public void An_Empty_Query_And_A_Zero_Count_Throw()
	{
		using TempFolder root = new();
		SearchTools tools = new(new FixedQuery(), Database, BuilderOver(root), Reducer);

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
