namespace FolderAssistant.Indexing;

/// <summary>Whether the folder index can answer a query yet.</summary>
internal enum IndexStatus
{
	/// <summary>The first index has not finished. Nothing is queryable.</summary>
	Building,

	/// <summary>The index is queryable.</summary>
	Ready,

	/// <summary>The index could not be built; the cause is on <see cref="IIndexState.Error"/>.</summary>
	Failed,
}

/// <summary>The readiness of the folder index, as a reader sees it.</summary>
internal interface IIndexState
{
	IndexStatus Status { get; }

	/// <summary>What went wrong, when <see cref="Status"/> is <see cref="IndexStatus.Failed"/>.</summary>
	Exception? Error { get; }

	/// <summary>When the index last completed, or null if it never has.</summary>
	DateTime? LastIndexedUtc { get; }
}

/// <summary>
/// Shared readiness of the folder index.
///
/// <para>
/// It exists so that retrieval has something to consult before it answers. Searching a half-built
/// index does not fail — it returns whatever happens to be stored so far, which is a plausible-looking
/// result set that is indistinguishable from a genuinely poor one. There is no signal in the scores
/// that says "ask again in a minute", so the only place that distinction can be made is here, where
/// whether the pass has finished is actually known.
/// </para>
///
/// <para>
/// Written by the indexing pass and read by retrieval, on different threads, so every access is
/// guarded. The state is three fields that move together: a status with no error and no timestamp
/// would be a state no reader could interpret.
/// </para>
/// </summary>
internal sealed class IndexState : IIndexState
{
	private readonly Object _gate = new();

	private IndexStatus _status = IndexStatus.Building;
	private Exception? _error;
	private DateTime? _lastIndexedUtc;

	public IndexStatus Status
	{
		get
		{
			lock (this._gate)
			{
				return this._status;
			}
		}
	}

	public Exception? Error
	{
		get
		{
			lock (this._gate)
			{
				return this._error;
			}
		}
	}

	public DateTime? LastIndexedUtc
	{
		get
		{
			lock (this._gate)
			{
				return this._lastIndexedUtc;
			}
		}
	}

	/// <summary>Records that a pass completed and the index can be queried.</summary>
	public void MarkReady(DateTime indexedUtc)
	{
		lock (this._gate)
		{
			this._status = IndexStatus.Ready;
			this._error = null;
			this._lastIndexedUtc = indexedUtc;
		}
	}

	/// <summary>
	/// Records that a pass failed, keeping the cause for the reader to report.
	///
	/// <para>
	/// Only a build that has never succeeded can move the index to <see cref="IndexStatus.Failed"/>. Once
	/// a pass has completed, a later one failing means the stored vectors are going stale, not that they
	/// have gone away — and a folder that is briefly unreadable would otherwise take a working index out
	/// of service and refuse queries it could still answer.
	/// </para>
	///
	/// <para>
	/// It records no error in that case either. <see cref="Error"/> is the reason the index is unusable,
	/// not a log of the last thing that went wrong, and publishing one against a Ready index makes a
	/// healthy index look broken to anything reading it as that signal.
	/// </para>
	/// </summary>
	public void MarkFailed(Exception error)
	{
		lock (this._gate)
		{
			if (this._status == IndexStatus.Ready)
			{
				return;
			}

			this._status = IndexStatus.Failed;
			this._error = error;
		}
	}
}
