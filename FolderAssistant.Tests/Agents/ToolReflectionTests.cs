using System.ComponentModel;
using System.Text.Json;
using FluentAssertions;
using FolderAssistant.Agents;
using FolderAssistant.Extraction;
using FolderAssistant.Retrieval;
using FolderAssistant.Tests.Tools;
using FolderAssistant.Tools;
using Microsoft.Extensions.AI;
using Moq;

namespace FolderAssistant.Tests.Agents;

/// <summary>
/// The reflection (SPEC-100): each holder's described public methods become functions named after them,
/// carrying the holder's own descriptions, with the cancellation token kept out of what the model sees;
/// and a reflected function really calls the holder.
/// </summary>
public sealed class ToolReflectionTests
{
	private const String Metadata = ".folderassistant";

	[Fact]
	public void Each_Holder_Yields_Its_Described_Methods_In_Declaration_Order()
	{
		using TempFolder root = new();
		ReadTools read = new(new WorkspacePathGuard(root.Path, Metadata), TextExtractorRegistry.Default, 1024);
		MutationTools mutate = new(new WorkspacePathGuard(root.Path, Metadata), new RecordingNotifier());
		SearchTools search = new(Mock.Of<IRetrievalQuery>(), "db");

		ToolReflection.Reflect(read).Select(tool => tool.Name).Should().Equal("InspectDirectory", "ReadFile", "Retrieve", "FindFiles", "SearchText");
		ToolReflection.Reflect(mutate).Select(tool => tool.Name).Should().Equal("Create", "Update", "ReplaceLines", "Delete");
		ToolReflection.Reflect(search).Select(tool => tool.Name).Should().Equal("FindFilesAbout");
	}

	[Fact]
	public void A_Function_Carries_The_Holders_Descriptions_And_Not_The_Cancellation_Token()
	{
		using TempFolder root = new();
		ReadTools read = new(new WorkspacePathGuard(root.Path, Metadata), TextExtractorRegistry.Default, 1024);

		AIFunction searchText = ToolReflection.Reflect(read).Single(tool => tool.Name == "SearchText");
		String schema = searchText.JsonSchema.GetRawText();

		searchText.Description.Should().StartWith("Searches the text files in the workspace");
		schema.Should().Contain("Match without regard to case.");
		schema.Should().Contain("\"pattern\"").And.Contain("\"wholeWord\"").And.Contain("\"path\"");
		schema.Should().NotContain("cancellationToken");
	}

	[Fact]
	public async Task A_Reflected_Function_Calls_The_Holder_And_Returns_Its_Result()
	{
		using TempFolder root = new();
		await File.WriteAllTextAsync(root.Combine("a.md"), "first line\nsecond line");
		ReadTools read = new(new WorkspacePathGuard(root.Path, Metadata), TextExtractorRegistry.Default, 1024);
		AIFunction readFile = ToolReflection.Reflect(read).Single(tool => tool.Name == "ReadFile");

		Object? result = await readFile.InvokeAsync(new AIFunctionArguments { ["path"] = "a.md", ["startLine"] = 2 });

		result.Should().BeOfType<JsonElement>();
		String json = ((JsonElement)result!).GetRawText();
		json.Should().Contain("second line").And.NotContain("first line");
		json.Should().MatchRegex("\"totalLines\":\\s*2");
	}

	[Fact]
	public void A_Holder_With_No_Described_Method_Is_Refused()
	{
		Action reflect = () => ToolReflection.Reflect(new Undescribed());

		reflect.Should().Throw<InvalidOperationException>().WithMessage("*Undescribed*no public method*");
	}

	[Fact]
	public void Only_Described_Public_Methods_Are_Tools()
	{
		ToolReflection.Reflect(new PartlyDescribed()).Select(tool => tool.Name).Should().Equal("Described");
	}

	internal sealed class Undescribed
	{
		private readonly String _reply = "not a tool";

		public String Plain() => this._reply;
	}

	internal sealed class PartlyDescribed
	{
		private readonly String _reply = "a tool";

		[Description("A tool.")]
		public String Described() => this._reply;

		public String Plain() => this._reply + " it is not";
	}
}
