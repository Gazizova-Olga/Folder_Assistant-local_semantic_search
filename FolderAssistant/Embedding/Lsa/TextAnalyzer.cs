using System.Text;

namespace FolderAssistant.Embedding.Lsa;

/// <summary>
/// Turns raw text into the normalized terms the corpus-fitted vectorizer counts.
///
/// <para>
/// It splits identifiers apart (<c>camelCase</c>, <c>snake_case</c>, letter/digit boundaries),
/// lowercases, drops a small stopword list, stems conservatively, and emits word unigrams plus
/// character trigrams.
/// </para>
///
/// <para>
/// The two term kinds do different jobs. Unigrams carry the distributional signal that becomes weak
/// synonymy after the reduction — the whole point of fitting. Trigrams give robustness to typos and
/// to shared morphology between related identifiers, which matters in a folder that is mostly code.
/// </para>
/// </summary>
internal sealed class TextAnalyzer
{
	private const String TrigramPrefix = "3:";

	private static readonly HashSet<String> Stopwords = new(StringComparer.Ordinal)
	{
		"the", "and", "for", "are", "but", "not", "you", "all", "any", "can", "has", "had", "was", "one",
		"our", "out", "use", "with", "this", "that", "from", "have", "will", "your", "they", "them", "then",
		"than", "into", "over", "such", "when", "what", "which", "while", "were", "been", "also", "each",
	};

	private readonly Boolean _includeTrigrams;

	public TextAnalyzer(Boolean includeTrigrams = true)
	{
		this._includeTrigrams = includeTrigrams;
	}

	public IReadOnlyList<String> Analyze(String text)
	{
		List<String> terms = [];

		if (String.IsNullOrWhiteSpace(text))
		{
			return terms;
		}

		foreach (String rawToken in SplitOnNonAlphanumeric(text))
		{
			foreach (String word in SplitIdentifier(rawToken))
			{
				String normalized = Stem(word.ToLowerInvariant());

				if (normalized.Length < 2 || Stopwords.Contains(normalized))
				{
					continue;
				}

				terms.Add(normalized);

				if (this._includeTrigrams)
				{
					AppendTrigrams(normalized, terms);
				}
			}
		}

		return terms;
	}

	private static IEnumerable<String> SplitOnNonAlphanumeric(String text)
	{
		StringBuilder buffer = new();

		foreach (Char character in text)
		{
			if (Char.IsLetterOrDigit(character))
			{
				buffer.Append(character);
			}
			else if (buffer.Length > 0)
			{
				yield return buffer.ToString();
				buffer.Clear();
			}
		}

		if (buffer.Length > 0)
		{
			yield return buffer.ToString();
		}
	}

	/// <summary>Splits <c>camelCase</c>, <c>PascalCase</c> and letter/digit boundaries inside one token.</summary>
	private static IEnumerable<String> SplitIdentifier(String token)
	{
		StringBuilder buffer = new();

		for (Int32 i = 0; i < token.Length; i++)
		{
			if (i > 0 && buffer.Length > 0 && IsBoundary(token[i - 1], token[i]))
			{
				yield return buffer.ToString();
				buffer.Clear();
			}

			buffer.Append(token[i]);
		}

		if (buffer.Length > 0)
		{
			yield return buffer.ToString();
		}
	}

	private static Boolean IsBoundary(Char previous, Char current)
	{
		// A lower or digit followed by an upper is camelCase; a letter next to a digit is the other case.
		if (Char.IsUpper(current) && !Char.IsUpper(previous))
		{
			return true;
		}

		return Char.IsDigit(current) != Char.IsDigit(previous);
	}

	private static void AppendTrigrams(String word, List<String> terms)
	{
		// Padded, so a trigram can encode "starts with" and "ends with" rather than only the interior.
		String padded = $"^{word}$";

		for (Int32 i = 0; i + 3 <= padded.Length; i++)
		{
			terms.Add(TrigramPrefix + padded.Substring(i, 3));
		}
	}

	/// <summary>
	/// Conservative suffix stripping, and deliberately mild. Determinism and collapsing the obvious
	/// inflections matter here; linguistic completeness does not, and over-stemming an identifier
	/// merges things that are genuinely different.
	/// </summary>
	private static String Stem(String word)
	{
		if (word.Length > 5 && word.EndsWith("ing", StringComparison.Ordinal))
		{
			return word[..^3];
		}

		if (word.Length > 4 && word.EndsWith("ed", StringComparison.Ordinal))
		{
			return word[..^2];
		}

		if (word.Length > 4 && word.EndsWith("es", StringComparison.Ordinal))
		{
			return word[..^2];
		}

		if (word.Length > 3
			&& word.EndsWith('s')
			&& !word.EndsWith("ss", StringComparison.Ordinal))
		{
			return word[..^1];
		}

		return word;
	}
}
