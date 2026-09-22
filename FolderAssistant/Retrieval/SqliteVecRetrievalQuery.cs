using FolderAssistant.Embedding;
using FolderAssistant.Indexing;
using FolderAssistant.Persistence;
using Microsoft.Data.Sqlite;

namespace FolderAssistant.Retrieval;

/// <summary>
/// Retrieval that pushes the ranking down into the query engine, through the native <c>sqlite-vec</c>
/// k-NN index.
///
/// <para>
/// This is the implementation <see cref="IRetrievalQuery"/> was shaped for. The contract says "return
/// the top k" rather than "return every vector so the caller can rank them" precisely so a backend like
/// this could exist without a caller changing — and until now that was a claim with nothing behind it.
/// </para>
///
/// <para>
/// It is the comparison candidate to <see cref="CosineRetrievalQuery"/>, not a replacement. Whether it
/// wins is a question it is now allowed to lose.
/// </para>
/// </summary>
internal sealed class SqliteVecRetrievalQuery : IRetrievalQuery
{
	private readonly IVectorizer _vectorizer;
	private readonly IVectorStoreReader _vectorStoreReader;
	private readonly IIndexState? _indexState;

	public SqliteVecRetrievalQuery(IVectorizer vectorizer, IIndexState? indexState = null)
		: this(vectorizer, new SqliteVecVectorStoreReader(), indexState)
	{
	}

	internal SqliteVecRetrievalQuery(
		IVectorizer vectorizer,
		IVectorStoreReader vectorStoreReader,
		IIndexState? indexState = null)
	{
		ArgumentNullException.ThrowIfNull(vectorizer);
		ArgumentNullException.ThrowIfNull(vectorStoreReader);

		this._vectorizer = vectorizer;
		this._vectorStoreReader = vectorStoreReader;
		this._indexState = indexState;
	}

	public IReadOnlyList<RetrievalHit> Search(String databasePath, String queryText, RetrievalOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);

		if (options.TopK <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(options), "TopK must be greater than zero.");
		}

		RetrievalGuard.EnsureQueryable(this._indexState);

		ModelDescriptor descriptor = this._vectorizer.Descriptor;

		// Embedded as a query, not as a document, for the same reason the baseline does it.
		IReadOnlyList<Single> queryVector = this._vectorizer.Vectorize(queryText, EmbeddingKind.Query).Vector;

		// The model version is not a filter here — it is the table. A vec0 table fixes its dimension at
		// creation, so each model version has its own, and searching one cannot reach another's vectors.
		String table = SqliteVecTable.NameFor(descriptor.ModelVersionId);

		using SqliteConnection connection =
			FolderDatabaseConnection.OpenRead(databasePath, withVectorExtension: true);

		if (!SqliteVecTable.ExistingTables(connection).Contains(table))
		{
			// The active model has never been indexed here. Nothing, rather than falling back to some
			// other model's vectors, which would be scoring across embedding spaces.
			return [];
		}

		List<(String ChunkId, Double Score)> ranked = new(options.TopK);

		using (SqliteCommand command = connection.CreateCommand())
		{
			command.CommandText =
				$"SELECT chunk_id, distance FROM {table} WHERE embedding MATCH $query AND k = $k;";
			command.Parameters.AddWithValue("$query", VectorBlob.Pack(queryVector));
			command.Parameters.AddWithValue("$k", options.TopK);

			using SqliteDataReader reader = command.ExecuteReader();
			while (reader.Read())
			{
				// The table declares distance_metric=cosine, so distance is 1 - cosine similarity.
				// Converting back keeps the score directly comparable with the baseline's, which is the
				// entire point of running the two side by side.
				Double score = 1.0 - reader.GetDouble(1);

				if (score >= options.MinScore)
				{
					ranked.Add((reader.GetString(0), score));
				}
			}
		}

		if (ranked.Count == 0)
		{
			return [];
		}

		// The same two-phase shape as the baseline: locations are resolved for ranked hits only. Here
		// the engine has already cut the candidates to k, so this is bounded by TopK by construction.
		IReadOnlyDictionary<String, ChunkLocation> locations = this._vectorStoreReader.ReadChunkLocations(
			databasePath,
			[.. ranked.Select(static hit => hit.ChunkId)]);

		List<RetrievalHit> hits = new(ranked.Count);

		foreach ((String chunkId, Double score) in ranked)
		{
			if (!locations.TryGetValue(chunkId, out ChunkLocation? location))
			{
				continue;
			}

			hits.Add(new RetrievalHit(
				location.ChunkId,
				location.FilePath,
				location.ChunkIndex,
				location.TokenStart,
				location.TokenEnd,
				score,
				location.ChunkHash));
		}

		return hits;
	}
}
