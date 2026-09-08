using System.Text.Json;

namespace FolderAssistant.Embedding.Lsa;

/// <summary>
/// A fitted model: the vocabulary, its inverse document frequencies, and the projection that maps a
/// weighted term vector into the reduced concept space.
///
/// <para>
/// This is what gets persisted per model version, so that a query is embedded with exactly the fit
/// that produced the vectors it will be compared against. Refitting a corpus produces a different
/// artifact and therefore belongs under a different model version.
/// </para>
/// </summary>
internal sealed record LsaModel(
	IReadOnlyList<String> Terms,
	IReadOnlyList<Single> Idf,
	IReadOnlyList<IReadOnlyList<Single>> Projection,
	Int32 Dimension,
	Boolean IncludeTrigrams)
{
	private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

	/// <summary>Term to column, rebuilt on load so a transform does not scan the vocabulary per term.</summary>
	public IReadOnlyDictionary<String, Int32> TermIndex { get; } = BuildIndex(Terms);

	public String Serialize()
		=> JsonSerializer.Serialize(
			new Payload(this.Terms, this.Idf, this.Projection, this.Dimension, this.IncludeTrigrams),
			SerializerOptions);

	public static LsaModel Deserialize(String json)
	{
		Payload payload = JsonSerializer.Deserialize<Payload>(json)
			?? throw new InvalidOperationException("The stored fit artifact deserialized to null.");

		return new LsaModel(
			payload.Terms,
			payload.Idf,
			payload.Projection,
			payload.Dimension,
			payload.IncludeTrigrams);
	}

	private static Dictionary<String, Int32> BuildIndex(IReadOnlyList<String> terms)
	{
		Dictionary<String, Int32> index = new(terms.Count, StringComparer.Ordinal);

		for (Int32 i = 0; i < terms.Count; i++)
		{
			index[terms[i]] = i;
		}

		return index;
	}

	// IncludeTrigrams is in the artifact rather than left to the caller, and that is the point: a query
	// analyzed differently from the corpus is projected from a different feature space. It would not
	// throw — it would just quietly retrieve the wrong things.
	private sealed record Payload(
		IReadOnlyList<String> Terms,
		IReadOnlyList<Single> Idf,
		IReadOnlyList<IReadOnlyList<Single>> Projection,
		Int32 Dimension,
		Boolean IncludeTrigrams);
}
