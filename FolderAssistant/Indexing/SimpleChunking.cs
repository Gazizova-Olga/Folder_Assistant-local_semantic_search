using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FolderAssistant.Indexing;

/// <summary>The tokens a file was split into.</summary>
internal sealed record TokenizedText(IReadOnlyList<String> Tokens);

/// <summary>One overlapping window of tokens, and the text it covers.</summary>
internal sealed record TextChunk(
	String ChunkId,
	Int32 Index,
	Int32 TokenStart,
	Int32 TokenEnd,
	String ChunkHash,
	String Content);

/// <summary>Splits text on whitespace. A token here is a run of non-space characters, nothing more.</summary>
internal sealed partial class SimpleTokenizer
{
	[GeneratedRegex(@"\S+")]
	private static partial Regex TokenPattern();

	public TokenizedText Tokenize(String content)
	{
		if (String.IsNullOrWhiteSpace(content))
		{
			return new TokenizedText([]);
		}

		return new TokenizedText(TokenPattern().Matches(content).Select(static match => match.Value).ToArray());
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
		IReadOnlyList<String> tokens,
		Int32 chunkSizeTokens,
		Int32 chunkOverlapTokens)
	{
		ArgumentNullException.ThrowIfNull(tokens);

		if (chunkSizeTokens <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(chunkSizeTokens), "Chunk size must be greater than zero.");
		}

		if (chunkOverlapTokens < 0 || chunkOverlapTokens >= chunkSizeTokens)
		{
			throw new ArgumentOutOfRangeException(
				nameof(chunkOverlapTokens), "Chunk overlap must be at least zero and less than the chunk size.");
		}

		if (tokens.Count == 0)
		{
			return [];
		}

		Int32 step = chunkSizeTokens - chunkOverlapTokens;
		List<TextChunk> chunks = [];

		for (Int32 start = 0, index = 0; start < tokens.Count; start += step, index++)
		{
			Int32 endExclusive = Math.Min(tokens.Count, start + chunkSizeTokens);
			String content = String.Join(" ", tokens.Skip(start).Take(endExclusive - start));
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
