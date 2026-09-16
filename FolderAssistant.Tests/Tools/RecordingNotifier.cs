using FolderAssistant.Indexing.Watching;

namespace FolderAssistant.Tests.Tools;

/// <summary>
/// A change notifier that records what it was told, in order, and can be made to fail — so a test can
/// assert both that a mutation reports itself with its own kind and that a report that fails never turns
/// the mutation into a failure.
/// </summary>
internal sealed class RecordingNotifier : IIndexChangeNotifier
{
	public List<(FileChangeKind Kind, String Path)> Reports { get; } = [];

	public Boolean Fails { get; set; }

	public ValueTask NotifyCreatedAsync(String absolutePath, CancellationToken cancellationToken = default)
		=> this.Record(FileChangeKind.Created, absolutePath);

	public ValueTask NotifyChangedAsync(String absolutePath, CancellationToken cancellationToken = default)
		=> this.Record(FileChangeKind.Modified, absolutePath);

	public ValueTask NotifyDeletedAsync(String absolutePath, CancellationToken cancellationToken = default)
		=> this.Record(FileChangeKind.Deleted, absolutePath);

	public IDisposable BeginBatch() => new NoHold();

	private ValueTask Record(FileChangeKind kind, String path)
	{
		if (this.Fails)
		{
			throw new InvalidOperationException("The notifier was made to fail.");
		}

		this.Reports.Add((kind, path));

		return ValueTask.CompletedTask;
	}

	private sealed class NoHold : IDisposable
	{
		public void Dispose()
		{
		}
	}
}
