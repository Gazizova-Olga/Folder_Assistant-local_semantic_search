using Microsoft.Data.Sqlite;

namespace FolderAssistant.Persistence;

/// <summary>
/// Writes one chunk's vector, inside a transaction the caller owns.
///
/// <para>
/// How a vector is stored is the one part of the write path with a real alternative — the same
/// vectors could sit in a different column type or a different table shape entirely, and swapping
/// that must not touch the file and chunk writes around it. The connection and transaction are
/// passed in rather than opened here so a vector lands in the same transaction as the chunk it
/// belongs to.
/// </para>
/// </summary>
internal interface IVectorStoreWriter
{
	void UpsertVector(
		SqliteConnection connection,
		SqliteTransaction transaction,
		String chunkId,
		String modelVersionId,
		IReadOnlyList<Single> vector,
		Int32 vectorDimension);

	/// <summary>
	/// Removes the vectors belonging to the given chunks, across every model version.
	///
	/// <para>
	/// Explicit rather than left to the <c>ON DELETE CASCADE</c> from <c>chunk_manifest</c>, which
	/// does currently fire. The cascade is a property of this backend and not of the contract: a
	/// native vector extension stores vectors in a virtual table, and a virtual table cannot be the
	/// target of a foreign key — so no such backend could ever provide one. Deleting through the
	/// interface is what keeps that swap possible.
	/// </para>
	/// </summary>
	void DeleteVectors(
		SqliteConnection connection,
		SqliteTransaction transaction,
		IReadOnlyList<String> chunkIds);
}

/// <summary>Stores a vector as a packed <c>float32</c> blob in <c>chunk_vector.vector</c>.</summary>
internal sealed class SqliteBlobVectorStoreWriter : IVectorStoreWriter
{
	public void UpsertVector(
		SqliteConnection connection,
		SqliteTransaction transaction,
		String chunkId,
		String modelVersionId,
		IReadOnlyList<Single> vector,
		Int32 vectorDimension)
	{
		ArgumentNullException.ThrowIfNull(connection);

		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = """
			INSERT INTO chunk_vector (
				chunk_id, model_version_id, vector, vector_dimension, updated_utc)
			VALUES ($chunkId, $modelVersionId, $vector, $dimension, $updated)
			ON CONFLICT(chunk_id, model_version_id) DO UPDATE SET
				vector = excluded.vector,
				vector_dimension = excluded.vector_dimension,
				updated_utc = excluded.updated_utc;
			""";
		command.Parameters.AddWithValue("$chunkId", chunkId);
		command.Parameters.AddWithValue("$modelVersionId", modelVersionId);
		command.Parameters.AddWithValue("$vector", VectorBlob.Pack(vector));
		command.Parameters.AddWithValue("$dimension", vectorDimension);
		command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
		command.ExecuteNonQuery();
	}

	public void DeleteVectors(
		SqliteConnection connection,
		SqliteTransaction transaction,
		IReadOnlyList<String> chunkIds)
	{
		ArgumentNullException.ThrowIfNull(connection);
		ArgumentNullException.ThrowIfNull(chunkIds);

		if (chunkIds.Count == 0)
		{
			return;
		}

		using SqliteCommand command = connection.CreateCommand();
		command.Transaction = transaction;
		command.CommandText = "DELETE FROM chunk_vector WHERE chunk_id = $chunkId;";

		SqliteParameter chunkId = command.Parameters.Add("$chunkId", SqliteType.Text);

		foreach (String id in chunkIds)
		{
			chunkId.Value = id;
			command.ExecuteNonQuery();
		}
	}
}
