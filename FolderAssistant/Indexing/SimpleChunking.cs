using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FolderAssistant.Indexing;

/// <summary>A token as a range into the text it came from, rather than a string cut out of it.</summary>
internal readonly record struct TokenSpan(Int32 Start, Int32 Length);

/// <summary>
/// The tokens a file was split into, held as offsets into the file's own text.
///
/// <para>
/// Cutting each token out as its own <see cref="String"/> is what this avoids, and the reason is
/// scale rather than tidiness: a token is a handful of characters, but an object each, and a corpus
/// runs to tens of millions of them. They existed only to be joined back together into chunk text,
/// out of the very string they were cut from.
/// </para>
/// </summary>
internal sealed record TokenizedText(String Source, IReadOnlyList<TokenSpan> Tokens)
{
	/// <summary>
	/// Lays token strings out single-space-separated, which is exactly how <see cref="TextChunker"/>
	/// reassembles chunk text. A test written against this drives the same code path the tokenizer
	/// feeds, rather than a parallel one that could agree with the wrong thing.
	/// </summary>
	public static TokenizedText FromTokens(IReadOnlyList<String> tokens)
	{
		ArgumentNullException.ThrowIfNull(tokens);

		List<TokenSpan> spans = new(tokens.Count);
		Int32 offset = 0;

		for (Int32 i = 0; i < tokens.Count; i++)
		{
			spans.Add(new TokenSpan(offset, tokens[i].Length));
			offset += tokens[i].Length + 1; // the separating space
		}

		return new TokenizedText(String.Join(" ", tokens), spans);
	}

	/// <summary>Materialises the tokens as strings — for tests and diagnostics, never on the index path.</summary>
	public IReadOnlyList<String> ToStrings()
	{
		String[] result = new String[this.Tokens.Count];

		for (Int32 i = 0; i < this.Tokens.Count; i++)
		{
			result[i] = this.Source.Substring(this.Tokens[i].Start, this.Tokens[i].Length);
		}

		return result;
	}
}

/// <summary>Splits text on whitespace. A token here is a run of non-space characters, nothing more.</summary>
internal sealed partial class SimpleTokenizer
{
	[GeneratedRegex(@"\S+")]
	private static partial Regex TokenPattern();

	public TokenizedText Tokenize(String content)
	{
		if (String.IsNullOrWhiteSpace(content))
		{
			return new TokenizedText(content ?? String.Empty, []);
		}

		// EnumerateMatches hands back each match's bounds without constructing a Match to carry them.
		// The pattern is the same one, so what counts as a token has not moved.
		List<TokenSpan> tokens = [];

		foreach (ValueMatch match in TokenPattern().EnumerateMatches(content))
		{
			tokens.Add(new TokenSpan(match.Index, match.Length));
		}

		return new TokenizedText(content, tokens);
	}
}

/// <summary>
/// Slides a fixed window over the tokens, overlapping each chunk with the one before it.
///
/// <para>
/// The overlap is what stops a passage that straddles a boundary from being unretrievable: without
/// it, a sentence split across two chunks appears in full in neither.
/// </para>
/// </summary>
internal sealed class TextChunker
{
	public IReadOnlyList<TextChunk> Chunk(
		String fileId,
		TokenizedText tokenized,
		Int32 chunkSizeTokens,
		Int32 chunkOverlapTokens)
	{
		ArgumentNullException.ThrowIfNull(tokenized);

		if (chunkSizeTokens <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(chunkSizeTokens), "Chunk size must be greater than zero.");
		}

		if (chunkOverlapTokens < 0 || chunkOverlapTokens >= chunkSizeTokens)
		{
			throw new ArgumentOutOfRangeException(
				nameof(chunkOverlapTokens), "Chunk overlap must be at least zero and less than the chunk size.");
		}

		IReadOnlyList<TokenSpan> tokens = tokenized.Tokens;

		if (tokens.Count == 0)
		{
			return [];
		}

		Int32 step = chunkSizeTokens - chunkOverlapTokens;
		List<TextChunk> chunks = [];
		StringBuilder builder = new();

		for (Int32 start = 0, index = 0; start < tokens.Count; start += step, index++)
		{
			Int32 endExclusive = Math.Min(tokens.Count, start + chunkSizeTokens);

			// A trailing window whose tokens the previous chunk already covers in full carries no
			// content of its own — it is a suffix of its predecessor, produced only because the step
			// happened to land there. Emitting it costs an embedding and lets the same text come back
			// twice in one result set. Only the final window can be redundant this way, because the
			// windows advance monotonically, so stopping here cannot skip a later one that is not.
			if (chunks.Count > 0 && endExclusive <= chunks[^1].TokenEnd)
			{
				break;
			}

			// Single-space-joined, character for character as before. The chunk text is normalised
			// rather than a verbatim slice of the source, and it has to stay that way: it feeds the
			// chunk hash, which feeds the content-addressed chunk id. Joining differently would give
			// every chunk in every indexed folder a new id, and the next pass would delete the stored
			// ones while the unchanged-file check declined to re-embed their replacements.
			builder.Clear();

			for (Int32 i = start; i < endExclusive; i++)
			{
				if (i > start)
				{
					builder.Append(' ');
				}

				builder.Append(tokenized.Source.AsSpan(tokens[i].Start, tokens[i].Length));
			}

			String content = builder.ToString();
			String chunkHash = Sha256(content);

			chunks.Add(new TextChunk(
				ChunkId: Sha256($"chunk::{fileId}::{index}::{chunkHash}"),
				Index: index,
				TokenStart: start,
				TokenEnd: endExclusive,
				ChunkHash: chunkHash,
				Content: content));
		}

		return chunks;
	}

	private static String Sha256(String value)
		=> Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
