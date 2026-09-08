using System.Text.Json;
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
}

/// <summary>Stores a vector as a JSON array in <c>chunk_vector.vector_json</c>.</summary>
internal sealed class SqliteJsonVectorStoreWriter : IVectorStoreWriter
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
				chunk_id, model_version_id, vector_json, vector_dimension, updated_utc)
			VALUES ($chunkId, $modelVersionId, $vectorJson, $dimension, $updated)
			ON CONFLICT(chunk_id, model_version_id) DO UPDATE SET
				vector_json = excluded.vector_json,
				vector_dimension = excluded.vector_dimension,
				updated_utc = excluded.updated_utc;
			""";
		command.Parameters.AddWithValue("$chunkId", chunkId);
		command.Parameters.AddWithValue("$modelVersionId", modelVersionId);
		command.Parameters.AddWithValue("$vectorJson", JsonSerializer.Serialize(vector));
		command.Parameters.AddWithValue("$dimension", vectorDimension);
		command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
		command.ExecuteNonQuery();
	}
}
