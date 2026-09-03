using System.Net;

namespace OnAirNative.Tests.Services;

/// <summary>
/// Minimal fake HttpMessageHandler for testing HttpClient-dependent services (AiChatService)
/// without a real network call. Captures the most recently sent request so tests can assert on
/// headers/URL/body, and returns a canned status code + response body configured up front.
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;
    private readonly string _responseBody;

    public HttpRequestMessage? LastRequest { get; private set; }

    /// <summary>The request body read eagerly during SendAsync — the caller's HttpRequestMessage
    /// (and its Content) is typically wrapped in a `using` and gets disposed the moment the real
    /// call returns, so reading LastRequest.Content afterwards throws ObjectDisposedException.
    /// Reading it here, before that disposal can happen, is what makes body assertions safe.</summary>
    public string? LastRequestBody { get; private set; }

    public FakeHttpMessageHandler(HttpStatusCode statusCode, string responseBody)
    {
        _statusCode = statusCode;
        _responseBody = responseBody;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var response = new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_responseBody),
        };
        return response;
    }
}
