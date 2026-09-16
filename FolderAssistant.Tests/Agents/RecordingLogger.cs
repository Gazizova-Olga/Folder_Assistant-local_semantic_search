using Microsoft.Extensions.Logging;

namespace FolderAssistant.Tests.Agents;

/// <summary>A logger that keeps every line it is given, so a test can assert what a call logged and at what level.</summary>
internal sealed class RecordingLogger : ILogger
{
	public List<(LogLevel Level, String Message, Exception? Exception)> Lines { get; } = [];

	public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

	public Boolean IsEnabled(LogLevel logLevel) => true;

	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, String> formatter)
		=> this.Lines.Add((logLevel, formatter(state, exception), exception));
}
