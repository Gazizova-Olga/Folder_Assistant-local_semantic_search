using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using FolderAssistant.Agents;
using FolderAssistant.Retrieval;

namespace FolderAssistant.Tests.Agents;

/// <summary>
/// The readable failure contract (SPEC-140): one sentence per kind of provider failure naming the key to
/// look at or the time to try again; the inner chain searched; anything not the provider's left alone.
/// </summary>
public sealed class ProviderErrorDescriberTests
{
	private static readonly DateTimeOffset Now = new(2026, 9, 23, 21, 12, 5, TimeSpan.Zero);

	private static ProviderErrorDescriber Describer() => new(new FixedClock(Now));

	[Theory]
	[InlineData(401, "Provider:ApiKey", "FolderAssistant__Provider__ApiKey")]
	[InlineData(403, "Provider:ApiKey", "FolderAssistant__Provider__ApiKey")]
	[InlineData(404, "Provider:DeploymentName", "Provider:Endpoint")]
	[InlineData(400, "Provider:DeploymentName", "HTTP 400")]
	[InlineData(503, "Try again later", "HTTP 503")]
	public void A_Status_Names_What_To_Look_At(Int32 status, String first, String second)
	{
		String sentence = Describer().Describe(new ClientResultException(new FakeResponse(status)))!;

		sentence.Should().Contain(first).And.Contain(second);
		sentence.Should().EndWith(".");
	}

	[Fact]
	public void A_Reason_Phrase_Is_Carried()
		=> Describer().Describe(new ClientResultException(new FakeResponse(502, "Bad Gateway")))
			.Should().Contain("HTTP 502 Bad Gateway");

	[Theory]
	[InlineData("120", "Wed, 23 Sep 2026 21:14:05 GMT")]
	[InlineData("Wed, 23 Sep 2026 22:00:00 GMT", "Wed, 23 Sep 2026 22:00:00 GMT")]
	[InlineData("soon", "soon")]
	public void Retry_After_Is_Said_As_An_Http_Date_Whichever_Form_It_Came_In(String header, String expected)
	{
		String sentence = Describer().Describe(new ClientResultException(new FakeResponse(429, headers: ("Retry-After", header))))!;

		sentence.Should().Be($"The provider is rate-limiting this client (HTTP 429); try again after {expected}.");
	}

	[Fact]
	public void A_Rate_Limit_Without_Retry_After_Says_So_Without_A_Time()
		=> Describer().Describe(new ClientResultException(new FakeResponse(429)))
			.Should().Be("The provider is rate-limiting this client (HTTP 429).");

	[Fact]
	public void A_Server_Failure_Carries_Retry_After_Too()
		=> Describer().Describe(new ClientResultException(new FakeResponse(503, headers: ("Retry-After", "60"))))
			.Should().Contain("try again after Wed, 23 Sep 2026 21:13:05 GMT");

	[Fact]
	public void A_Transport_That_Could_Not_Reach_The_Server_Points_At_The_Endpoint()
	{
		String sentence = Describer().Describe(new HttpRequestException("No connection could be made because the target machine actively refused it."))!;

		sentence.Should().Be("The provider could not be reached: No connection could be made because the target machine actively refused it. Check that Provider:Endpoint is right and the server is running.");
	}

	[Fact]
	public void A_Transport_Status_Is_Reported_As_The_Servers_Word()
		=> Describer().Describe(new HttpRequestException("gone", null, HttpStatusCode.BadGateway))
			.Should().Be("The provider answered HTTP 502 (BadGateway). Check Provider:Endpoint.");

	[Fact]
	public void A_Deadline_Names_The_Timeout_Whether_It_Came_As_A_Timeout_Or_A_Cancellation()
	{
		String? fromTimeout = Describer().Describe(new TimeoutException());
		String? fromCancellation = Describer().Describe(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

		fromTimeout.Should().Contain("ConnectionTimeoutSeconds");
		fromCancellation.Should().Be(fromTimeout);
	}

	/// <summary>The SDK wraps: a result exception with no status over a request exception over a socket error.</summary>
	[Fact]
	public void The_Inner_Chain_Is_Searched_For_The_First_Thing_Recognised()
	{
		SocketException socket = new((Int32)SocketError.ConnectionRefused);
		HttpRequestException http = new("wrapped", socket);
		ClientResultException outer = new("Service request failed.", new FakeResponse(0), http);

		Describer().Describe(outer).Should().StartWith("The provider could not be reached: wrapped.");
		Describer().Describe(new InvalidOperationException("outer", socket)).Should().Contain("ConnectionRefused");
	}

	[Fact]
	public void What_Is_Not_The_Providers_Is_Left_With_Its_Own_Message()
	{
		IndexNotReadyException refusal = new("The index is still building.");
		InvalidOperationException fault = new("a tool's own fault");

		Describer().Describe(refusal).Should().BeNull();
		Describer().Describe(fault).Should().BeNull();
		Describer().DescribeOrMessage(refusal).Should().Be("The index is still building.");
		Describer().DescribeOrMessage(new ClientResultException(new FakeResponse(401))).Should().Contain("Provider:ApiKey");
	}

	private sealed class FixedClock(DateTimeOffset now) : TimeProvider
	{
		public override DateTimeOffset GetUtcNow() => now;
	}

	/// <summary>Enough of a pipeline response for a result exception: the status, the reason and the headers.</summary>
	private sealed class FakeResponse(Int32 status, String reason = "", params (String Name, String Value)[] headers) : PipelineResponse
	{
		public override Int32 Status => status;

		public override String ReasonPhrase => reason;

		public override Stream? ContentStream { get; set; }

		public override BinaryData Content => BinaryData.Empty;

		protected override PipelineResponseHeaders HeadersCore => new FakeHeaders(headers);

		public override BinaryData BufferContent(CancellationToken cancellationToken = default) => BinaryData.Empty;

		public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(BinaryData.Empty);

		public override void Dispose()
		{
			// Holds nothing.
		}
	}

	private sealed class FakeHeaders((String Name, String Value)[] headers) : PipelineResponseHeaders
	{
		public override IEnumerator<KeyValuePair<String, String>> GetEnumerator()
			=> headers.Select(header => new KeyValuePair<String, String>(header.Name, header.Value)).GetEnumerator();

		public override Boolean TryGetValue(String name, out String? value)
		{
			value = headers.Where(header => String.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase)).Select(header => header.Value).FirstOrDefault();

			return value is not null;
		}

		public override Boolean TryGetValues(String name, out IEnumerable<String>? values)
		{
			values = this.TryGetValue(name, out String? value) ? [value!] : null;

			return values is not null;
		}
	}
}
