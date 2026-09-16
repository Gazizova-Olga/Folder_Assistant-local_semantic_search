using FolderAssistant.Embedding;
using FolderAssistant.Extraction;
using FolderAssistant.Indexing;
using FolderAssistant.Indexing.Outbox;
using FolderAssistant.Persistence;

namespace FolderAssistant.Tests;

/// <summary>
/// Delivers everything the outbox holds, through the real dispatcher and the real bridge — the path
/// a running application takes once its whole-folder pass has finished, run to completion here so a
/// test can assert what the index looks like after the front end has caught up.
///
/// <para>
/// The whole-folder pass records removals and queues them; it deletes nothing itself, because only a
/// delivered removal can clear a file's vectors before its row. A test that asserts a deleted file is
/// gone therefore drains the queue the way the front end would, rather than through a shortcut the
/// application does not have.
/// </para>
/// </summary>
internal static class OutboxDrain
{
	/// <summary>Drains until a pass claims nothing. Returns how many operations were claimed in all.</summary>
	public static Int32 Deliver(
		String rootPath,
		String databasePath,
		IndexingConfig config,
		IVectorizer? vectorizer = null,
		IVectorStoreWriter? writer = null,
		IVectorStoreReader? reader = null)
	{
		FolderIndexStore store = new(databasePath);

		using RagBridgeVectorizationService bridge = new(
			rootPath,
			databasePath,
			vectorizer ?? new ProgrammableEmbeddingVectorizer(config.ModelVersionId, config.VectorDimension),
			new FolderIndexRepository(writer ?? new SqliteBlobVectorStoreWriter()),
			reader ?? new SqliteBlobVectorStoreReader(),
			TextExtractorRegistry.Default,
			config,
			new PersistenceConfig().MetadataFolderName);

		OutboxDispatcher dispatcher = new(rootPath, store, bridge);

		Int32 delivered = 0;
		Int32 claimed;

		do
		{
			claimed = dispatcher.DrainOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
			delivered += claimed;
		}
		while (claimed > 0);

		return delivered;
	}
}
