using System.Runtime.CompilerServices;
using FolderAssistant.Indexing.Watching;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace FolderAssistant.Agents;

/// <summary>
/// Holds the index's batch for the whole of an agent run (SPEC-100, SPEC-121): every file the run's
/// tools change is published to the indexer once, when the run ends, rather than once per step of the
/// model's thinking. An agent that writes a file, reads it back and writes it again would otherwise
/// cost the index a pass per write, each landing in its own quiet window.
///
/// <para>
/// The boundary is the agent run and not a transport, because a transport outlives many turns — an SSE
/// connection stays open across a conversation — and a hold that lasted for one would stop the index
/// following the folder for as long as a browser tab stayed open. Applied to every agent in the roster,
/// so a delegate's run holds inside its caller's; holds nest and the last release publishes. A streamed
/// run holds until its enumeration is disposed, which is when the caller has stopped reading, and a run
/// that fails releases on its way out. A hold nobody releases expires on its own (SPEC-121), so a leaked
/// enumerator costs a bounded delay and never an index that stops converging.
/// </para>
/// </summary>
internal sealed class BatchHoldingAgent : DelegatingAIAgent
{
	private readonly IIndexChangeNotifier _indexer;

	public BatchHoldingAgent(AIAgent inner, IIndexChangeNotifier indexer)
		: base(inner)
	{
		ArgumentNullException.ThrowIfNull(indexer);
		this._indexer = indexer;
	}

	protected override async Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
	{
		using IDisposable hold = this._indexer.BeginBatch();

		return await base.RunCoreAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
	}

	protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		using IDisposable hold = this._indexer.BeginBatch();

		await foreach (AgentResponseUpdate update in base.RunCoreStreamingAsync(messages, session, options, cancellationToken).ConfigureAwait(false))
		{
			yield return update;
		}
	}
}
