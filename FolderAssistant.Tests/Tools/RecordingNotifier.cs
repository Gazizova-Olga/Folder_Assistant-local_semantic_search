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

	/// <summary>How many holds were open when each report was made, in order — a report inside an agent run is held.</summary>
	public List<Int32> HoldsAtReport { get; } = [];

	public Boolean Fails { get; set; }

	public ValueTask NotifyCreatedAsync(String absolutePath, CancellationToken cancellationToken = default)
		=> this.Record(FileChangeKind.Created, absolutePath);

	public ValueTask NotifyChangedAsync(String absolutePath, CancellationToken cancellationToken = default)
		=> this.Record(FileChangeKind.Modified, absolutePath);

	public ValueTask NotifyDeletedAsync(String absolutePath, CancellationToken cancellationToken = default)
		=> this.Record(FileChangeKind.Deleted, absolutePath);

	/// <summary>How many holds are open right now, and the most that were ever open at once.</summary>
	public Int32 OpenHolds { get; private set; }

	public Int32 DeepestHold { get; private set; }

	/// <summary>Every hold ever begun, so a test can tell "one hold for the run" from "none".</summary>
	public Int32 HoldsBegun { get; private set; }

	public IDisposable BeginBatch()
	{
		this.HoldsBegun++;
		this.OpenHolds++;
		this.DeepestHold = Math.Max(this.DeepestHold, this.OpenHolds);

		return new Hold(this);
	}

	private ValueTask Record(FileChangeKind kind, String path)
	{
		if (this.Fails)
		{
			throw new InvalidOperationException("The notifier was made to fail.");
		}

		this.Reports.Add((kind, path));
		this.HoldsAtReport.Add(this.OpenHolds);

		return ValueTask.CompletedTask;
	}

	private sealed class Hold(RecordingNotifier owner) : IDisposable
	{
		private Boolean _released;

		public void Dispose()
		{
			if (!this._released)
			{
				this._released = true;
				owner.OpenHolds--;
			}
		}
	}
}
