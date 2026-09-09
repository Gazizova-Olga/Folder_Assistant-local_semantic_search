using System.Buffers.Binary;

namespace FolderAssistant.Persistence;

/// <summary>
/// Packs a vector into a little-endian <c>float32</c> blob, and back.
///
/// <para>
/// This is the stored representation of every chunk vector (schema version 3). It replaced JSON
/// text, which was not a neutral choice of encoding: a 64-dimension vector is 256 bytes packed
/// against roughly 700-800 bytes of JSON that then has to be parsed into floats one component at
/// a time. Measurements are in <c>SPEC-131</c>.
/// </para>
///
/// <para>
/// Little-endian is written explicitly rather than relying on <see cref="BitConverter"/>'s host
/// order, so a database file stays readable if it is ever opened on a big-endian machine.
/// </para>
/// </summary>
internal static class VectorBlob
{
	private const Int32 BytesPerComponent = sizeof(Single);

	public static Byte[] Pack(IReadOnlyList<Single> vector)
	{
		ArgumentNullException.ThrowIfNull(vector);

		Byte[] bytes = new Byte[vector.Count * BytesPerComponent];
		Span<Byte> span = bytes.AsSpan();

		for (Int32 i = 0; i < vector.Count; i++)
		{
			BinaryPrimitives.WriteSingleLittleEndian(span.Slice(i * BytesPerComponent, BytesPerComponent), vector[i]);
		}

		return bytes;
	}

	public static Single[] Unpack(ReadOnlySpan<Byte> blob)
	{
		if (blob.Length % BytesPerComponent != 0)
		{
			// Not a recoverable condition: a partial component means the stored bytes are not what
			// this code wrote, and guessing at the remainder would return a plausible wrong vector.
			throw new InvalidOperationException(
				$"A stored vector is {blob.Length} bytes, which is not a whole number of " +
				$"{BytesPerComponent}-byte components. The index is corrupt and must be rebuilt.");
		}

		Single[] vector = new Single[blob.Length / BytesPerComponent];

		for (Int32 i = 0; i < vector.Length; i++)
		{
			vector[i] = BinaryPrimitives.ReadSingleLittleEndian(blob.Slice(i * BytesPerComponent, BytesPerComponent));
		}

		return vector;
	}
}
