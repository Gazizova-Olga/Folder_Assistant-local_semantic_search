using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using System.Net.Sockets;

namespace FolderAssistant.Agents;

/// <summary>
/// Turns a provider's failure into one sentence that says what happened and what to do (SPEC-140).
/// The SDK's own messages are a status line, a wire-format error body and a stack of inner exceptions;
/// what a person at a console or a browser needs is which configuration key to look at, or when to try
/// again.
///
/// <para>
/// It describes only what it recognises — an HTTP status from the provider, a transport that could not
/// reach it, a deadline — and answers null for anything else, so a failure that is not the provider's
/// (a search refusal, a tool's own fault) keeps its own message rather than being misfiled as one.
/// </para>
/// </summary>
internal sealed class ProviderErrorDescriber
{
	private readonly TimeProvider _clock;

	public ProviderErrorDescriber(TimeProvider? clock = null)
	{
		this._clock = clock ?? TimeProvider.System;
	}

	/// <summary>
	/// One sentence for <paramref name="exception"/> when it is the provider's, looking down the inner
	/// chain for the first thing recognised; null when nothing in it is.
	/// </summary>
	public String? Describe(Exception exception)
	{
		ArgumentNullException.ThrowIfNull(exception);

		for (Exception? current = exception; current is not null; current = current.InnerException)
		{
			String? described = current switch
			{
				ClientResultException { Status: > 0 } result => this.DescribeStatus(result),
				HttpRequestException http => DescribeTransport(http),
				SocketException socket => $"The provider could not be reached ({socket.SocketErrorCode}). Check that Provider:Endpoint is right and the server is running.",
				TimeoutException or OperationCanceledException => "The provider did not answer within the configured ConnectionTimeoutSeconds. Check that the server is running and not overloaded, or raise the timeout.",
				_ => null,
			};

			if (described is not null)
			{
				return described;
			}
		}

		return null;
	}

	/// <summary>The sentence, or the exception's own message when it is not the provider's.</summary>
	public String DescribeOrMessage(Exception exception)
	{
		ArgumentNullException.ThrowIfNull(exception);

		return this.Describe(exception) ?? exception.Message;
	}

	private static String DescribeTransport(HttpRequestException http)
	{
		// The status is the server's word, and a server that answered is a different problem from one that
		// could not be reached; the SDK reports the first as a result exception, so a status here is rare.
		if (http.StatusCode is { } status)
		{
			return $"The provider answered HTTP {(Int32)status} ({status}). Check Provider:Endpoint.";
		}

		return $"The provider could not be reached: {http.Message.TrimEnd('.')}. Check that Provider:Endpoint is right and the server is running.";
	}

	private String DescribeStatus(ClientResultException result)
	{
		Int32 status = result.Status;
		PipelineResponse? response = result.GetRawResponse();

		return status switch
		{
			401 or 403 => $"The provider refused the credentials (HTTP {status}). Check Provider:ApiKey, or the environment variable FolderAssistant__Provider__ApiKey.",
			404 => $"The provider has no such model or deployment (HTTP 404). Check Provider:DeploymentName, and Provider:Endpoint for a local server.",
			429 => $"The provider is rate-limiting this client (HTTP 429){this.RetryAfter(response)}.",
			>= 500 => $"The provider failed on its side (HTTP {status}{Reason(response)}){this.RetryAfter(response)}. Try again later; nothing here is misconfigured.",
			_ => $"The provider rejected the request (HTTP {status}{Reason(response)}). The request the agent made was not one the model accepts: check Provider:DeploymentName and the sampling options.",
		};
	}

	private static String Reason(PipelineResponse? response)
		=> String.IsNullOrWhiteSpace(response?.ReasonPhrase) ? String.Empty : $" {response.ReasonPhrase}";

	/// <summary>
	/// The server's <c>Retry-After</c>, as an HTTP date whichever form it came in: a person reading "try
	/// again after Tue, 23 Sep 2026 21:14:05 GMT" can act on it, where "after 120" can only be counted from
	/// a moment they did not see.
	/// </summary>
	private String RetryAfter(PipelineResponse? response)
	{
		if (response is null || !response.Headers.TryGetValue("Retry-After", out String? value) || String.IsNullOrWhiteSpace(value))
		{
			return String.Empty;
		}

		if (Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out Int32 seconds))
		{
			return $"; try again after {this._clock.GetUtcNow().AddSeconds(seconds).ToString("R", CultureInfo.InvariantCulture)}";
		}

		if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset at))
		{
			return $"; try again after {at.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture)}";
		}

		return $"; try again after {value}";
	}
}
