using System.Net;
using System.Net.Http;
using Tamga.Sdk;
using Xunit;

namespace Tamga.Sdk.Tests;

/// <summary>
/// The server rate-limits; the SDK has to cope.
/// </summary>
/// <remarks>
/// Credential-accepting endpoints run on a tight per-IP budget (5 requests/second by default), and
/// the calls a licensing client makes on a timer — validate, heartbeat ping, check-in — are exactly
/// the ones inside it. Without backoff, a retry loop turns one throttled request into a sustained
/// burst that keeps the bucket empty and the client never recovers on its own.
/// </remarks>
public class RateLimitTests
{
    [Fact]
    public void ACreateIsNeverAutoRetried()
    {
        // Repeating a create is not safe: the first attempt may well have succeeded server-side,
        // and a second activation burns a second seat.
        Assert.False(TamgaTransport.IsRetryable(HttpMethod.Post, "/v1/accounts/acc/machines"));
        Assert.False(TamgaTransport.IsRetryable(HttpMethod.Post, "/v1/accounts/acc/licenses"));
    }

    [Fact]
    public void TheCallsMadeOnATimerAreRetryable()
    {
        Assert.True(TamgaTransport.IsRetryable(HttpMethod.Get, "/v1/accounts/acc/licenses"));
        Assert.True(TamgaTransport.IsRetryable(HttpMethod.Post, "/v1/accounts/acc/licenses/actions/validate"));
        Assert.True(TamgaTransport.IsRetryable(HttpMethod.Post, "/v1/accounts/acc/machines/x/actions/ping"));
    }

    [Fact]
    public void OtherMethodsAreNotRetried()
    {
        Assert.False(TamgaTransport.IsRetryable(HttpMethod.Delete, "/v1/accounts/acc/machines/x"));
        Assert.False(TamgaTransport.IsRetryable(HttpMethod.Patch, "/v1/accounts/acc/licenses/x"));
    }

    [Fact]
    public void ASaneRetryAfterIsHonoured()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), TamgaTransport.RetryDelay(0, 5));
    }

    [Fact]
    public void AnAbsurdRetryAfterIsCapped()
    {
        // A misconfigured — or hostile — proxy must not be able to park the caller for a day on a
        // single header.
        Assert.True(TamgaTransport.RetryDelay(0, 86_400) <= TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void BackoffGrowsWhenTheServerSaysNothing()
    {
        // Guessing the same short delay every time is just the original burst again.
        Assert.True(TamgaTransport.RetryDelay(2, null) > TamgaTransport.RetryDelay(0, null));
    }

    [Fact]
    public void AnHttpDateRetryAfterFallsBackRatherThanBeingMisread()
    {
        // Parsing a date as a duration would be far worse than backing off.
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", "Wed, 21 Oct 2026 07:28:00 GMT");
        Assert.Null(TamgaTransport.ParseRetryAfter(response));
    }
}
