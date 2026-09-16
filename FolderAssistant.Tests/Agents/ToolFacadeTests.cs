using FluentAssertions;
using FolderAssistant.Agents;
using FolderAssistant.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FolderAssistant.Tests.Agents;

/// <summary>
/// The facade (SPEC-100), one contract per group: a file tool's failure is the string the model must
/// report, a search tool's failure is the exception itself, a cancellation is neither — and every call
/// leaves one log line saying which.
/// </summary>
public sealed class ToolFacadeTests
{
	private static AIFunction Throwing(String name, Exception exception)
		=> AIFunctionFactory.Create(new Func<String, String>(_ => throw exception), name);

	[Fact]
	public async Task A_File_Tools_Failure_Is_A_String_Naming_The_Tool_And_The_Cause()
	{
		RecordingLogger logger = new();
		ToolFacade facade = new(Throwing("ReadFile", new FileNotFoundException("'x.md' is not a file in the workspace.")), ToolGroup.File, logger);

		Object? result = await facade.InvokeAsync(new AIFunctionArguments { ["_"] = "x.md" });

		result.Should().Be("TOOL_FAILED: ReadFile: 'x.md' is not a file in the workspace.");
		logger.Lines.Should().ContainSingle();
		logger.Lines[0].Level.Should().Be(LogLevel.Warning);
		logger.Lines[0].Message.Should().Contain("name=ReadFile").And.Contain("group=File").And.Contain("status=Failed").And.Contain("error=FileNotFoundException");
		logger.Lines[0].Exception.Should().BeOfType<FileNotFoundException>();
	}

	[Fact]
	public async Task A_Search_Tools_Failure_Is_The_Exception_Itself()
	{
		RecordingLogger logger = new();
		IndexNotReadyException refusal = new("The index is still building.");
		ToolFacade facade = new(Throwing("FindFilesAbout", refusal), ToolGroup.Search, logger);

		Func<Task> invoke = async () => await facade.InvokeAsync(new AIFunctionArguments { ["_"] = "topic" });

		(await invoke.Should().ThrowAsync<IndexNotReadyException>()).Which.Should().BeSameAs(refusal);
		logger.Lines.Should().ContainSingle();
		logger.Lines[0].Level.Should().Be(LogLevel.Error);
		logger.Lines[0].Message.Should().Contain("name=FindFilesAbout").And.Contain("group=Search").And.Contain("status=Failed");
	}

	[Fact]
	public async Task A_Success_Passes_Through_With_One_Line_At_Information()
	{
		RecordingLogger logger = new();
		AIFunction inner = AIFunctionFactory.Create((String path) => $"contents of {path}", "Retrieve", "Reads a file.");
		ToolFacade facade = new(inner, ToolGroup.File, logger);

		Object? result = await facade.InvokeAsync(new AIFunctionArguments { ["path"] = "a.md" });

		result!.ToString().Should().Be("contents of a.md");
		facade.Name.Should().Be("Retrieve");
		facade.Description.Should().Be("Reads a file.");
		facade.JsonSchema.GetRawText().Should().Be(inner.JsonSchema.GetRawText());
		logger.Lines.Should().ContainSingle();
		logger.Lines[0].Level.Should().Be(LogLevel.Information);
		logger.Lines[0].Message.Should().Contain("name=Retrieve").And.Contain("group=File").And.Contain("status=Success").And.MatchRegex(@"latencyMs=\d+\.\d");
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task A_Cancellation_Of_The_Callers_Token_Passes_Through_Both_Groups(Boolean fileGroup)
	{
		ToolGroup group = fileGroup ? ToolGroup.File : ToolGroup.Search;
		RecordingLogger logger = new();
		AIFunction inner = AIFunctionFactory.Create((CancellationToken cancellationToken) =>
		{
			cancellationToken.ThrowIfCancellationRequested();

			return "never";
		}, "Slow");
		ToolFacade facade = new(inner, group, logger);
		using CancellationTokenSource cancelled = new();
		await cancelled.CancelAsync();

		Func<Task> invoke = async () => await facade.InvokeAsync(new AIFunctionArguments(), cancelled.Token);

		await invoke.Should().ThrowAsync<OperationCanceledException>();
		logger.Lines.Should().ContainSingle();
		logger.Lines[0].Level.Should().Be(LogLevel.Information);
		logger.Lines[0].Message.Should().Contain("status=Cancelled");
	}

	/// <summary>
	/// The classification is by the caller's token, not the exception type: a cancellation nobody asked
	/// for — a transport's own deadline reported as one — is a failure under the group's contract.
	/// </summary>
	[Fact]
	public async Task A_Cancellation_Nobody_Asked_For_Is_A_Failure()
	{
		RecordingLogger logger = new();
		ToolFacade facade = new(Throwing("ReadFile", new OperationCanceledException("the transport gave up")), ToolGroup.File, logger);

		Object? result = await facade.InvokeAsync(new AIFunctionArguments { ["_"] = "x" });

		result.Should().Be("TOOL_FAILED: ReadFile: the transport gave up");
		logger.Lines[0].Message.Should().Contain("status=Failed");
	}
}
