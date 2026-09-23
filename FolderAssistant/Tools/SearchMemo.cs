namespace FolderAssistant.Tools;

/// <summary>
/// The per-turn memoization of the passage search (SPEC-101): an identical <c>(query, maxResults)</c>
/// asked twice in one turn is answered once. A model retrying the same call after a tool failure, or
/// asking the same thing on two branches of its reasoning, otherwise costs a second embed, a second
/// scan and a second read of every file, and could get a different answer if a file moved between.
///
/// <para>
/// The scope is an <see cref="AsyncLocal{T}"/> the turn's execution opens, so the memo follows the
/// turn through the framework's tool-calling loop and no further: nothing is cached across turns, and
/// outside a scope every search runs. It bounds a query cache and nothing else — session state is the
/// execution's, and a scope opened unconditionally is what keeps that true.
/// </para>
/// </summary>
internal static class SearchMemo
{
	private static readonly AsyncLocal<Dictionary<(String Query, Int32 MaxResults), SemanticSearchResult>?> Current = new();

	/// <summary>Opens a scope for the turn on this async flow; disposing it ends the memo.</summary>
	public static IDisposable BeginScope()
	{
		Current.Value = new Dictionary<(String, Int32), SemanticSearchResult>();

		return new Scope();
	}

	/// <summary>Whether a scope is open on this flow — for a test to prove the turn opened one.</summary>
	internal static Boolean IsOpen => Current.Value is not null;

	public static Boolean TryGet(String query, Int32 maxResults, out SemanticSearchResult? result)
	{
		result = null;

		return Current.Value is { } memo && memo.TryGetValue((query, maxResults), out result);
	}

	public static void Set(String query, Int32 maxResults, SemanticSearchResult result)
	{
		if (Current.Value is { } memo)
		{
			memo[(query, maxResults)] = result;
		}
	}

	private sealed class Scope : IDisposable
	{
		public void Dispose() => Current.Value = null;
	}
}
