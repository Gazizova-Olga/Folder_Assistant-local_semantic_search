using System.Diagnostics.CodeAnalysis;

namespace FolderAssistant.Embedding;

/// <summary>
/// The one rule about where embedding may send document text (<c>SPEC-000</c>, <c>SPEC-162</c>): the
/// endpoint an embedding profile calls must be on loopback, unless the operator has said in
/// configuration that it need not be.
///
/// <para>
/// Every profile in this binary is offline, and for the in-process ones that is a property of the
/// assembly — there is no endpoint to point anywhere. The Ollama profiles trade that for real
/// semantics: they open a socket, and the guarantee becomes a property of the endpoint instead. An
/// endpoint read from free-form configuration with nothing checking it is not a guarantee at all,
/// which is what this closes: the text of every indexed file goes to whatever host that value names.
/// </para>
///
/// <para>
/// The rule is textual and deliberately conservative — <see cref="Uri.IsLoopback"/> on the parsed
/// value, no name resolution. Resolving would be a network call at startup, and a host name that
/// resolves to <c>127.0.0.1</c> today can resolve elsewhere tomorrow, so the check would assert
/// something it cannot keep. An alias for the local machine is therefore refused like any other
/// name, and <see cref="IndexingConfig.AllowRemoteEmbeddingEndpoint"/> is how an operator who means
/// it says so.
/// </para>
/// </summary>
internal static class EmbeddingEndpointGuard
{
	/// <summary>The setting holding the endpoint, spelled as an operator sees it in configuration.</summary>
	public const String EndpointSetting = "Indexing:OllamaEndpoint";

	/// <summary>The setting that permits a remote one.</summary>
	public const String AllowSetting = "Indexing:AllowRemoteEmbeddingEndpoint";

	/// <summary>
	/// Refuses an endpoint that would send document text off this machine. Called by the vectorizer's
	/// own constructor, so no composition path can reach a remote server without passing here, and by
	/// the composition root, so the refusal is a startup failure rather than a failed first index.
	/// </summary>
	/// <exception cref="InvalidOperationException">
	/// The endpoint is not an absolute http(s) URL, or is not on loopback while
	/// <paramref name="allowRemote"/> is false. The message names the setting to change.
	/// </exception>
	public static void Validate(String endpoint, Boolean allowRemote)
	{
		Uri uri = Parse(endpoint);

		if (!uri.IsLoopback && !allowRemote)
		{
			throw new InvalidOperationException(
				$"{EndpointSetting} is '{endpoint}', which is not a loopback address. Embedding sends the text of "
				+ "every indexed file to that endpoint, and this system promises that text does not leave the "
				+ "machine, so the configuration is refused rather than run. Point it at localhost, or set "
				+ $"{AllowSetting} to true (environment variable "
				+ "FolderAssistant__Indexing__AllowRemoteEmbeddingEndpoint) to make that trade deliberately.");
		}
	}

	/// <summary>
	/// What the root endpoint says about this configuration: <c>null</c> while the promise holds, and a
	/// sentence once an operator has opted out of it. The opt-out is reported for the same reason the
	/// default profile's store fallback is — a system running something other than what its
	/// documentation promises has to say so where anyone can see it, not only in a startup log line
	/// that has scrolled away.
	/// </summary>
	public static String? RemotePermissionNote(String endpoint, Boolean allowRemote)
	{
		// A malformed endpoint says nothing here. Validate owns that refusal, and reporting it from the
		// root endpoint as well would mean this method throwing out of a GET on a host that started.
		// The parse has to be the same one Validate uses, or the two disagree about a value like
		// "localhost:11434", which is an absolute URI whose scheme is "localhost" and is not loopback.
		if (!allowRemote || !TryParseHttp(endpoint, out Uri? uri) || uri.IsLoopback)
		{
			return null;
		}

		return $"{AllowSetting} is set: {EndpointSetting} is '{endpoint}', which is not loopback, so the text of "
			+ "indexed files is sent to that host when an ollama-* profile is active.";
	}

	/// <summary>
	/// An absolute http(s) URL, or a refusal. A value that is not a URL at all is refused here rather
	/// than allowed through to fail later as a transport error, which reads as an unreachable server and
	/// sends the operator looking in the wrong place.
	/// </summary>
	private static Uri Parse(String endpoint)
	{
		if (!TryParseHttp(endpoint, out Uri? uri))
		{
			throw new InvalidOperationException(
				$"{EndpointSetting} is '{endpoint}', which is not an absolute http or https URL. It is where the "
				+ "embedding model is served, such as http://localhost:11434/v1.");
		}

		return uri;
	}

	/// <summary>
	/// The scheme test is load-bearing rather than tidy: <c>localhost:11434</c> parses as an absolute
	/// URI whose scheme is <c>localhost</c>, so a check that only parsed would call it remote and one
	/// that only looked at the host would call it loopback.
	/// </summary>
	private static Boolean TryParseHttp(String endpoint, [NotNullWhen(true)] out Uri? uri)
	{
		return Uri.TryCreate(endpoint, UriKind.Absolute, out uri)
			&& (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
	}
}
