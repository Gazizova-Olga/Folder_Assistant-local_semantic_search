using FolderAssistant.Tools;
using Microsoft.Extensions.AI;

namespace FolderAssistant.Agents;

/// <summary>
/// The tools an agent may be handed, each holder reflected and wrapped in the facade of its group
/// (SPEC-100). The holder decides the group here, by type, so that a search holder cannot be wrapped
/// under the file contract by a call site that got the argument order wrong: the split is the contract
/// SPEC-101 built the holders apart for, and this is the one place it is applied.
/// </summary>
internal static class ToolSet
{
	/// <summary>The file tools — the read holder's and the mutation holder's — under the string contract.</summary>
	public static IReadOnlyList<AITool> ForFiles(ReadTools read, MutationTools mutate, ILogger logger)
	{
		ArgumentNullException.ThrowIfNull(read);
		ArgumentNullException.ThrowIfNull(mutate);

		return Wrap([.. ToolReflection.Reflect(read), .. ToolReflection.Reflect(mutate)], ToolGroup.File, logger);
	}

	/// <summary>The search tools under the fatal contract.</summary>
	public static IReadOnlyList<AITool> ForSearch(SearchTools search, ILogger logger)
	{
		ArgumentNullException.ThrowIfNull(search);

		return Wrap(ToolReflection.Reflect(search), ToolGroup.Search, logger);
	}

	private static List<AITool> Wrap(IReadOnlyList<AIFunction> functions, ToolGroup group, ILogger logger)
		=> [.. functions.Select(function => new ToolFacade(function, group, logger))];
}
