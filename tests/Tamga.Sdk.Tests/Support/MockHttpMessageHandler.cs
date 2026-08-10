using System.Net;
using System.Text;

namespace Tamga.Sdk.Tests.Support;

/// <summary>
/// No-live-network <see cref="HttpMessageHandler"/> test double. Records every request it
/// receives and returns pre-configured (or dynamically-computed) responses — shared across every
/// client/transport test in this project so no test ever makes a real HTTP call.
/// </summary>
public sealed class MockHttpMessageHandler : HttpMessageHandler
{
    public sealed record Recorded(HttpRequestMessage Request, string? Body);

    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders = new();
    public List<Recorded> Requests { get; } = new();

    /// <summary>Enqueues a fixed response for the next request received.</summary>
    public void Enqueue(HttpStatusCode status, string body, string contentType = "application/vnd.api+json")
    {
        Enqueue(_ => MakeResponse(status, body, contentType));
    }

    /// <summary>Enqueues a response computed from the incoming request (for assertions that need to inspect it before responding).</summary>
    public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responders.Enqueue(responder);
    }

    public static HttpResponseMessage MakeResponse(HttpStatusCode status, string body, string contentType = "application/vnd.api+json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = null;
        if (request.Content is not null)
        {
            body = request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
        }

        Requests.Add(new Recorded(request, body));

        if (_responders.Count == 0)
        {
            throw new InvalidOperationException("MockHttpMessageHandler received a request with no queued response.");
        }

        return Task.FromResult(_responders.Dequeue()(request));
    }
}
