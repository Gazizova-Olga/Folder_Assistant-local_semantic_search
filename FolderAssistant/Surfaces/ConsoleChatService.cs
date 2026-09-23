using FolderAssistant.Agents;

namespace FolderAssistant.Surfaces;

/// <summary>
/// Runs the console loop beside the web host (SPEC-100). Started with the other hosted services and
/// off the startup path — the first read blocks until somebody types — it runs <see cref="ConsoleChatLoop"/>
/// over the streams the root registered and does one of three things when the loop ends: <c>exit</c>
/// stops the host with it; the end of standard input leaves the host serving, said once in the log; the
/// host's own shutdown needs nothing. Over a redirected standard input the loop is not started at all, so
/// a service, a container or a <c>nohup</c> run keeps serving with nobody at the console.
///
/// <para>
/// Nothing the loop does can stop the host except <c>exit</c>: a fault in the loop itself is logged at
/// error and the host goes on. A background service that threw would stop the host by the framework's
/// default, and a console that took the web host down with it is the failure the plan names.
/// </para>
/// </summary>
internal sealed class ConsoleChatService : BackgroundService
{
	private readonly Func<WorkflowRunner> _runner;
	private readonly ConsoleStreams _streams;
	private readonly AgentConfig _config;
	private readonly IHostApplicationLifetime _lifetime;
	private readonly ILogger<ConsoleChatService> _logger;

	/// <param name="runner">
	/// Resolved on the first question, never here: building the runner builds every agent's client, and a
	/// configuration naming no provider is refused there. Taken at construction, that refusal would stop the
	/// host at boot, and a keyless host must boot and serve its index (SPEC-100).
	/// </param>
	public ConsoleChatService(
		Func<WorkflowRunner> runner,
		ConsoleStreams streams,
		AgentConfig config,
		IHostApplicationLifetime lifetime,
		ILogger<ConsoleChatService> logger)
	{
		ArgumentNullException.ThrowIfNull(runner);
		ArgumentNullException.ThrowIfNull(streams);
		ArgumentNullException.ThrowIfNull(config);
		ArgumentNullException.ThrowIfNull(lifetime);
		ArgumentNullException.ThrowIfNull(logger);
		this._runner = runner;
		this._streams = streams;
		this._config = config;
		this._lifetime = lifetime;
		this._logger = logger;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		if (this._streams.InputRedirected)
		{
			this._logger.LogInformation("Console chat not started: standard input is redirected. The host serves until it is stopped.");
			return;
		}

		// Off the startup path: the host must not wait on the first line being typed.
		await Task.Yield();

		try
		{
			ConsoleChatLoop loop = new(() => this._runner(), this._streams, this._config.ResolveAnalyzedFolderPath(), this._logger);
			ConsoleLoopEnd end = await loop.RunAsync(stoppingToken).ConfigureAwait(false);

			switch (end)
			{
				case ConsoleLoopEnd.ExitRequested:
					this._logger.LogInformation("Console chat ended on '{Exit}'; stopping the host.", ConsoleChatLoop.ExitCommand);
					this._lifetime.StopApplication();
					break;
				case ConsoleLoopEnd.InputEnded:
					this._logger.LogInformation("Console chat ended: standard input closed. The host serves until it is stopped.");
					break;
				default:
					break;
			}
		}
		catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
		{
			this._logger.LogError(ex, "The console chat stopped on a fault; the host serves until it is stopped.");
		}
	}
}
