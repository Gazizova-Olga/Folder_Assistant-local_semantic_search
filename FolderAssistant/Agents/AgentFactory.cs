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
/// </summary>
internal static class AgentFactory
{
	internal const String DefaultName = "Folder Assistant";
	internal const String DefaultDescription = "answers questions about the contents of one folder on this machine";

	/// <summary>
	/// The agent over <paramref name="client"/>, with <paramref name="tools"/> as what it may call.
	/// The handle owns the client, so disposing the handle ends the client.
	/// </summary>
	public static AgentHandle Create(AgentConfig config, IChatClient client, IReadOnlyList<AITool> tools, ILoggerFactory? loggerFactory = null)
	{
		ArgumentNullException.ThrowIfNull(config);
		ArgumentNullException.ThrowIfNull(client);
		ArgumentNullException.ThrowIfNull(tools);

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

		return new AgentHandle(name, new ChatClientAgent(client, options, loggerFactory), client);
	}

	/// <summary>The system prompt: the configured one verbatim, or one built from the name and description.</summary>
	internal static String Instructions(AgentConfig config, String name, String description)
	{
		if (!String.IsNullOrWhiteSpace(config.SystemPrompt))
		{
			return config.SystemPrompt;
		}

		return $"You are {name}, an assistant that {description}. "
			+ "Answer from the folder's files through the tools you are given, and say so when they do not hold the answer. "
			+ "Never present something you did not find in the folder as if it came from it.";
	}
}
