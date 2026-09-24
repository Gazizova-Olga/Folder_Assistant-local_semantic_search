using FluentAssertions;
using FolderAssistant.Embedding;

namespace FolderAssistant.Tests;

/// <summary>
/// Where embedding is allowed to send document text.
///
/// <para>
/// This is the one check standing between the system's headline promise — the text of your files does
/// not leave the machine — and a configuration value that was free-form with nothing reading it. The
/// tests below are written against the two ways that promise can be broken: an endpoint nobody looked
/// at, and an opt-out that is taken silently rather than reported.
/// </para>
/// </summary>
public sealed class EmbeddingEndpointGuardTests
{
	/// <summary>
	/// The forms a local server is actually named by. <c>localhost</c> is the default; the two literals
	/// are what a machine with an unusual hosts file or an IPv6 stack ends up writing; <c>127.0.0.2</c>
	/// is the rest of the loopback range, which is loopback and has to stay allowed.
	/// </summary>
	[Theory]
	[InlineData("http://localhost:11434/v1")]
	[InlineData("http://127.0.0.1:11434/v1")]
	[InlineData("http://127.0.0.2:11434/v1")]
	[InlineData("http://[::1]:11434/v1")]
	[InlineData("https://localhost:11434/v1")]
	public void A_Loopback_Endpoint_Is_Allowed(String endpoint)
	{
		Action validate = () => EmbeddingEndpointGuard.Validate(endpoint, allowRemote: false);

		validate.Should().NotThrow();
		EmbeddingEndpointGuard.RemotePermissionNote(endpoint, allowRemote: true).Should().BeNull();
	}

	/// <summary>
	/// The defect this closes. Without the check the endpoint is free-form configuration, so every
	/// indexed file's text goes wherever it points, while SPEC-000 promises it cannot. The message has to
	/// name both settings: the one that is wrong, and the one that makes it deliberate.
	/// </summary>
	[Theory]
	[InlineData("http://gpu.example.com:11434/v1")]
	[InlineData("https://embeddings.example.com/v1")]
	[InlineData("http://192.168.1.50:11434/v1")]
	public void A_Remote_Endpoint_Is_Refused_With_Both_Settings_Named(String endpoint)
	{
		Action validate = () => EmbeddingEndpointGuard.Validate(endpoint, allowRemote: false);

		validate.Should().Throw<InvalidOperationException>()
			.Which.Message.Should()
				.Contain(endpoint).And
				.Contain("Indexing:OllamaEndpoint").And
				.Contain("Indexing:AllowRemoteEmbeddingEndpoint");
	}

	/// <summary>
	/// A local machine's own name is not loopback by this rule, and that is the deliberate part: deciding
	/// otherwise means resolving the name, which is a network call at startup asserting something that
	/// can change under it. The opt-out is how an operator whose endpoint really is local says so.
	/// </summary>
	[Fact]
	public void A_Host_Name_That_Is_Not_Loopback_Is_Refused_Even_If_It_Resolves_Locally()
	{
		String endpoint = $"http://{Environment.MachineName}:11434/v1";

		Action validate = () => EmbeddingEndpointGuard.Validate(endpoint, allowRemote: false);

		validate.Should().Throw<InvalidOperationException>();
	}

	/// <summary>
	/// The opt-out permits the endpoint and is reported, in that order of importance. An opt-out that
	/// worked but said nothing would leave a system whose documentation promises one thing and whose
	/// behaviour is another, with nothing anywhere saying which is running.
	/// </summary>
	[Fact]
	public void The_Opt_Out_Permits_A_Remote_Endpoint_And_Is_Reported()
	{
		const String Endpoint = "http://gpu.example.com:11434/v1";

		Action validate = () => EmbeddingEndpointGuard.Validate(Endpoint, allowRemote: true);

		validate.Should().NotThrow();
		EmbeddingEndpointGuard.RemotePermissionNote(Endpoint, allowRemote: true).Should()
			.Contain(Endpoint).And.Contain("Indexing:AllowRemoteEmbeddingEndpoint");
		EmbeddingEndpointGuard.RemotePermissionNote(Endpoint, allowRemote: false).Should().BeNull();
	}

	/// <summary>
	/// A value that is not a URL is refused here rather than left to fail as a transport error later,
	/// which reads as an unreachable server and sends the operator to look at the wrong thing. The
	/// opt-out permits a remote host, not a meaningless one.
	/// </summary>
	[Theory]
	[InlineData("localhost:11434")]
	[InlineData("/v1/embeddings")]
	[InlineData("ftp://localhost/v1")]
	[InlineData("not a url at all")]
	public void A_Value_That_Is_Not_An_Http_Url_Is_Refused_Either_Way(String endpoint)
	{
		Action strict = () => EmbeddingEndpointGuard.Validate(endpoint, allowRemote: false);
		Action permissive = () => EmbeddingEndpointGuard.Validate(endpoint, allowRemote: true);

		strict.Should().Throw<InvalidOperationException>().Which.Message.Should().Contain("http");
		permissive.Should().Throw<InvalidOperationException>();

		// The root endpoint has to survive the same value: the note is silent about it rather than
		// throwing out of a GET on a host that started under a profile which never reads the endpoint.
		EmbeddingEndpointGuard.RemotePermissionNote(endpoint, allowRemote: true).Should().BeNull();
	}

	/// <summary>
	/// The check belongs where it cannot be walked around. Composition is not the narrowest point — a
	/// second call site, a test helper or a later profile could each build this vectorizer directly — so
	/// the constructor that turns an endpoint into a client is what refuses, before a client exists.
	/// </summary>
	[Fact]
	public void The_Vectorizer_Refuses_To_Be_Built_Against_A_Remote_Endpoint()
	{
		Action build = () => new OllamaEmbeddingVectorizer(
			"http://gpu.example.com:11434/v1",
			"qwen3-embedding:0.6b",
			"qwen3-embedding-0.6b-v1",
			1024,
			TimeSpan.FromSeconds(120),
			allowRemoteEndpoint: false);

		build.Should().Throw<InvalidOperationException>()
			.Which.Message.Should().Contain("Indexing:AllowRemoteEmbeddingEndpoint");
	}

	/// <summary>
	/// And permits it when the operator has said so — otherwise the opt-out would be a setting that
	/// changes a startup message and nothing else.
	/// </summary>
	[Fact]
	public void The_Vectorizer_Builds_Against_A_Remote_Endpoint_Once_It_Is_Permitted()
	{
		using OllamaEmbeddingVectorizer vectorizer = new(
			"http://gpu.example.com:11434/v1",
			"qwen3-embedding:0.6b",
			"qwen3-embedding-0.6b-v1",
			1024,
			TimeSpan.FromSeconds(120),
			allowRemoteEndpoint: true);

		vectorizer.Descriptor.ModelVersionId.Should().Be("qwen3-embedding-0.6b-v1");
	}
}
