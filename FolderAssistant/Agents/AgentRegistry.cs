using System.ComponentModel;
using System.Text.RegularExpressions;
using FolderAssistant.Indexing.Watching;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace FolderAssistant.Agents;

/// <summary>
/// One handle per agent in the roster (SPEC-100), each built over its own client from its effective
/// provider, holding the tools its entry allows and one delegation tool per agent it may delegate to.
///
/// <para>
/// A delegation tool is named after its target and described by the target's description, so what the
/// model reads is what the target says it does, written once. Calling it runs the target agent on the
/// request alone — no session, nothing of the caller's conversation — and returns the target's text.
/// It is under the fatal contract: a delegate's turn that fails, a search fault or a provider that could
/// not be reached, ends the caller's turn too, because the caller answering over a swallowed failure is
/// the same plausible wrong answer one level up.
/// </para>
///
/// <para>
/// Building the registry builds every client, so a configuration that cannot name a provider fails here,
/// when the registry is first asked for, with the sentence SPEC-140 specifies. The roster itself was
/// validated at startup; this is the half that needs a provider.
/// </para>
/// </summary>
internal sealed partial class AgentRegistry : IDisposable
{
	private readonly Dictionary<String, AgentHandle> _handles = new(StringComparer.Ordinal);
	private readonly List<AgentHandle> _inOrder = [];

	/// <param name="roster">The validated roster.</param>
	/// <param name="catalog">Every tool the application can grant.</param>
	/// <param name="clients">Builds a chat client for an agent's effective provider.</param>
	/// <param name="loggerFactory">Optional; the delegation facades and the agents log through it.</param>
	/// <param name="indexer">Optional; with it every agent holds the index's batch for the whole of each run.</param>
	/// <param name="history">Optional; with it every agent keeps its conversation's messages there instead of in its session.</param>
	public AgentRegistry(Roster roster, AgentToolCatalog catalog, Func<ProviderConfig, IChatClient> clients, ILoggerFactory? loggerFactory = null, IIndexChangeNotifier? indexer = null, ChatHistoryProvider? history = null)
	{
		ArgumentNullException.ThrowIfNull(roster);
		ArgumentNullException.ThrowIfNull(catalog);
		ArgumentNullException.ThrowIfNull(clients);

		ILogger logger = loggerFactory is null ? NullLogger.Instance : loggerFactory.CreateLogger<ToolFacade>();
		try
		{
			foreach (AgentDefinition definition in roster.Agents)
			{
				List<AITool> tools =
				[
					.. catalog.Select(definition.Tools),
					.. definition.Delegates.Select(target => new ToolFacade(this.DelegationTo(roster[target]), ToolGroup.Delegation, logger)),
				];

				// Every agent gets the same provider instance: it holds no session state of its own, and which
				// conversation a run belongs to comes from the session it is handed (SPEC-170).
				AgentHandle handle = AgentFactory.Create(
					definition.Name,
					definition.Description,
					definition.SystemPrompt,
					clients(definition.Provider),
					tools,
					loggerFactory,
					indexer,
					history);

				this._handles.Add(definition.Name, handle);
				this._inOrder.Add(handle);
			}
		}
		catch
		{
			// A registry half built holds clients nothing will ever dispose; end them before the failure
			// leaves, so a bad provider on the third agent does not leak the first two.
			this.Dispose();
			throw;
		}
	}

	/// <summary>The handles, in roster order.</summary>
	public IReadOnlyList<AgentHandle> Handles => this._inOrder;

	/// <summary>The handle for a roster name. The roster was validated, so an unknown name is a caller's bug.</summary>
	public AgentHandle Get(String name) => this._handles[name];

	/// <summary>The name of the tool that delegates to <paramref name="target"/>: the target's name, made safe for a tool.</summary>
	internal static String DelegationToolName(String target) => "delegate_to_" + UnsafeToolNameCharacters().Replace(target, "_");

	public void Dispose()
	{
		foreach (AgentHandle handle in this._inOrder)
		{
			handle.Dispose();
		}
	}

	private AIFunction DelegationTo(AgentDefinition target)
	{
		// The target is looked up when the tool runs, not when it is built, so roster order does not
		// matter: by the first turn every handle exists, and the roster refused any cycle at startup.
		String name = target.Name;

		return AIFunctionFactory.Create(
			async ([Description("What to ask, in full. The agent sees only this request and nothing of the conversation it came from.")] String request, CancellationToken cancellationToken) =>
			{
				AgentResponse response = await this.Get(name).Agent.RunAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);

				return response.Text;
			},
			DelegationToolName(name),
			target.Description);
	}

	[GeneratedRegex("[^A-Za-z0-9_-]")]
	private static partial Regex UnsafeToolNameCharacters();
}
