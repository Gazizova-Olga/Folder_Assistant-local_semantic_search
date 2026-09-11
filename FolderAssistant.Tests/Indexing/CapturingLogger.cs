using Microsoft.Extensions.Logging;

namespace FolderAssistant.Tests.Indexing;

/// <summary>One recorded call, flattened to the parts a test has anything to say about.</summary>
internal sealed record LoggedLine(LogLevel Level, string Message, Exception? Exception);

/// <summary>
/// An <see cref="ILogger{T}"/> that keeps what it is told instead of writing it anywhere.
///
/// <para>
/// Every loop in the indexing library survives its faults by design, and that is precisely what makes
/// the survival unobservable: a pass that failed and a pass that had nothing to do leave the same state
/// behind them. The recorded line is the only difference between those two, so it is the thing these
/// tests can assert on — which also means asserting on the level, not just on the text, because the
/// level is the part that decides whether anyone ever reads it.
/// </para>
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
	private readonly List<LoggedLine> _lines = [];
	private readonly Lock _gate = new();

	/// <summary>A copy: the loops under test write from their own threads while a test reads.</summary>
	public IReadOnlyList<LoggedLine> Lines
	{
		get { lock (_gate) { return [.. _lines]; } }
	}

	public IReadOnlyList<LoggedLine> At(LogLevel level) => [.. Lines.Where(line => line.Level == level)];

	public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

	/// <summary>
	/// Always on. A real provider filters by level, and a test that let it would be asserting the
	/// filter's configuration rather than what the library chose to record.
	/// </summary>
	public bool IsEnabled(LogLevel logLevel) => true;

	public void Log<TState>(
		LogLevel logLevel,
		EventId eventId,
		TState state,
		Exception? exception,
		Func<TState, Exception?, string> formatter)
	{
		ArgumentNullException.ThrowIfNull(formatter);

		lock (_gate)
		{
			_lines.Add(new LoggedLine(logLevel, formatter(state, exception), exception));
		}
	}
}
