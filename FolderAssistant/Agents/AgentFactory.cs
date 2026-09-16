using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FolderAssistant.Agents;

/// <summary>
/// Builds the agent from the configuration and a chat client (SPEC-100). The agent is the framework's
/// chat-client agent: the name, description and instructions are its role, the tools are what it may
/// call, and the client is where its turns go.
///
/// <para>
/// The instructions are the configured system prompt when there is one, and otherwise a prompt built
/// from the agent's name and description — so an operator who sets neither still gets an agent that
/// knows it answers about one folder, and one who sets the prompt gets exactly that prompt and nothing
/// added to it.
/// </para>
///
/// <para>
/// The tool-calling loop is built here rather than left to the agent's default, because its one setting
/// that matters is the failure contract's other half. The framework's loop catches a tool that throws and
/// hands the model a generic error string, which is exactly the swallowed fault the search group refuses;
/// so the loop is told to tolerate no failed iteration, and a tool that throws through its facade ends the
/// turn. The file tools never throw — their facade returns the string the model must report — so the
/// setting reaches only the group it is meant for.
/// </para>
/// </summary>
internal static class AgentFactory
{
	internal const String DefaultName = "Folder Assistant";
	internal const String DefaultDescription = "answers questions about the contents of one folder on this machine";

	/// <summary>
	/// The agent over <paramref name="client"/>, with <paramref name="tools"/> as what it may call.
	/// The handle owns the client, so disposing the handle ends the client. Two tools with one name are
	/// refused: a model told of both could not say which it meant.
	/// </summary>
	public static AgentHandle Create(AgentConfig config, IChatClient client, IReadOnlyList<AITool> tools, ILoggerFactory? loggerFactory = null)
	{
		ArgumentNullException.ThrowIfNull(config);
		ArgumentNullException.ThrowIfNull(client);
		ArgumentNullException.ThrowIfNull(tools);

		String[] duplicates = [.. tools
			.GroupBy(static tool => tool.Name, StringComparer.Ordinal)
			.Where(static group => group.Count() > 1)
			.Select(static group => group.Key)];
		if (duplicates.Length > 0)
		{
			throw new InvalidOperationException($"Two tools share one name: {String.Join(", ", duplicates)}. A model cannot tell them apart.");
		}

		String name = String.IsNullOrWhiteSpace(config.AgentName) ? DefaultName : config.AgentName;
		String description = String.IsNullOrWhiteSpace(config.AgentDescription) ? DefaultDescription : config.AgentDescription;

		ChatClientAgentOptions options = new()
		{
			Name = name,
			Description = description,
			ChatOptions = new ChatOptions
			{
				Instructions = Instructions(config, name, description),
				Tools = tools.Count == 0 ? null : [.. tools],
			},
		};

		IChatClient invoking = WithFunctionInvocation(client, loggerFactory);

		return new AgentHandle(name, new ChatClientAgent(invoking, options, loggerFactory), invoking, tools);
	}

	/// <summary>
	/// The tool-calling loop over <paramref name="client"/>, tolerating no failed iteration: a tool that
	/// throws ends the turn with its exception rather than becoming a generic error the model reads on.
	/// The agent sees the loop is already there and adds none of its own.
	/// </summary>
	internal static IChatClient WithFunctionInvocation(IChatClient client, ILoggerFactory? loggerFactory)
		=> new ChatClientBuilder(client)
			.UseFunctionInvocation(loggerFactory, static invoker => invoker.MaximumConsecutiveErrorsPerRequest = 0)
			.Build();

	/// <summary>The system prompt: the configured one verbatim, or one built from the name and description.</summary>
	internal static String Instructions(AgentConfig config, String name, String description)
	{
		if (!String.IsNullOrWhiteSpace(config.SystemPrompt))
		{
			return config.SystemPrompt;
		}

		return $"You are {name}, an assistant that {description}. "
			+ "Answer from the folder's files through the tools you are given, and say so when they do not hold the answer. "
			+ "Never present something you did not find in the folder as if it came from it. "
			+ $"A tool result that begins with {ToolFacade.FailurePrefix.Trim()} is a failure: report it to the user as such, and do not answer around it.";
	}
}
