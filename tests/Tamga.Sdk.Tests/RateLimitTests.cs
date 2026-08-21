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

    /// <summary>
    /// Heartbeat writes must be retried. Neither <c>/actions/ping-heartbeat</c> nor
    /// <c>/actions/reset-heartbeat</c> ends with the <c>/actions/ping</c> suffix — that one is the
    /// PROCESS ping route — so both fell outside the retry list, and a throttled heartbeat was
    /// dropped silently. A dropped heartbeat flips a machine to <c>DEAD</c>, and on a policy that
    /// actually sets <c>require_heartbeat</c> (it defaults to <c>FALSE</c>) eventually gets it
    /// culled. Both are bare idempotent state writes server-side, so repeating them cannot burn a
    /// seat.
    /// </summary>
    [Fact]
    public void HeartbeatWritesAreRetryable()
    {
        Assert.True(TamgaTransport.IsRetryable(HttpMethod.Post, "/v1/accounts/acc/machines/m-1/actions/ping-heartbeat"));
        Assert.True(TamgaTransport.IsRetryable(HttpMethod.Post, "/v1/accounts/acc/machines/m-1/actions/reset-heartbeat"));
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
    public void TheDefaultTimeoutSitsOutsideTheServersOwnDeadline()
    {
        // The server applies a 30s TimeoutLayer of its own. Matching it exactly makes the two race,
        // and a slow request then usually surfaces as a local cancellation rather than the
        // server's 504 — which is the response that actually carries the X-Request-Id a support
        // ticket needs.
        var options = new TamgaClientOptions { AccountId = "acct-1", BaseUrl = "https://api.tamga.test" };

        Assert.True(options.Timeout > TimeSpan.FromSeconds(30), $"default timeout was {options.Timeout}");
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
