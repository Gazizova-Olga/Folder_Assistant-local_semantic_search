using FluentAssertions;
using FolderAssistant.Persistence;

namespace FolderAssistant.Tests;

/// <summary>
/// The stored representation of every vector. A silent error here is the worst kind available:
/// the numbers still come back, retrieval still ranks, and the ranking is quietly wrong.
/// </summary>
public sealed class VectorBlobTests
{
	[Fact]
	public void A_Packed_Vector_Unpacks_To_What_Went_In()
	{
		Single[] vector = [0.5f, -0.25f, 0f, 1f, -1f];

		VectorBlob.Unpack(VectorBlob.Pack(vector)).Should().Equal(vector);
	}

	/// <summary>Four bytes per component, so the size is arithmetic rather than a property of the values.</summary>
	[Theory]
	[InlineData(0, 0)]
	[InlineData(1, 4)]
	[InlineData(64, 256)]
	[InlineData(1536, 6144)]
	public void The_Packed_Size_Is_Four_Bytes_Per_Component(Int32 dimension, Int32 expectedBytes)
		=> VectorBlob.Pack(new Single[dimension]).Should().HaveCount(expectedBytes);

	/// <summary>
	/// Written little-endian explicitly rather than in host order, so a database file stays readable
	/// if it is ever opened on a big-endian machine. Pinned against the byte pattern, because host
	/// order happens to be little-endian here and a host-order bug would pass any round-trip test.
	/// </summary>
	[Fact]
	public void Components_Are_Written_Little_Endian()
	{
		// 1.0f is 0x3F800000; little-endian that is 00 00 80 3F.
		VectorBlob.Pack([1.0f]).Should().Equal(0x00, 0x00, 0x80, 0x3F);
	}

	[Fact]
	public void Special_Values_Survive_The_Round_Trip()
	{
		Single[] vector = [Single.Epsilon, Single.MaxValue, Single.MinValue, -0f];

		VectorBlob.Unpack(VectorBlob.Pack(vector)).Should().Equal(vector);
	}

	/// <summary>
	/// A partial component means the stored bytes are not what this code wrote. Guessing at the
	/// remainder would hand retrieval a plausible wrong vector, so it says the index is corrupt.
	/// </summary>
	[Theory]
	[InlineData(1)]
	[InlineData(3)]
	[InlineData(7)]
	public void A_Blob_That_Is_Not_A_Whole_Number_Of_Components_Is_Rejected(Int32 byteCount)
	{
		Func<Object> unpack = () => VectorBlob.Unpack(new Byte[byteCount]);

		unpack.Should().Throw<InvalidOperationException>().WithMessage("*corrupt*");
	}

	[Fact]
	public void An_Empty_Vector_Round_Trips_As_Empty()
		=> VectorBlob.Unpack(VectorBlob.Pack([])).Should().BeEmpty();
}
