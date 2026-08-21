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

    /// <summary>
    /// The <c>x-ratelimit-*</c> headers ARE set. This SDK documented them as declared-in-CORS but
    /// never sent until 2026-08-21, and that was simply false: the rate-limit middleware attaches
    /// all four to the response it returns (<c>shared/rate_limit/middleware.rs:140-143</c>) — on
    /// the request it lets through as well as on the <c>429</c> it refuses.
    /// </summary>
    [Fact]
    public void TheRateLimitHeadersAreReadBack_IncludingWindow()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("x-ratelimit-limit", "60");
        response.Headers.Add("x-ratelimit-remaining", "59");
        response.Headers.Add("x-ratelimit-reset", "1755772800");
        response.Headers.Add("x-ratelimit-window", "1");

        var info = TamgaTransport.ReadRateLimitInfo(response);

        Assert.Equal(60, info.Limit);
        Assert.Equal(59, info.Remaining);
        Assert.Equal(1755772800, info.Reset);
        Assert.Equal(1, info.Window);
        Assert.True(info.IsPresent);
    }

    /// <summary>
    /// <c>reset</c> is an absolute Unix time, not a delay — the server computes it as
    /// <c>now + ttl</c> (<c>shared/rate_limit/mod.rs:80</c>). Reading it as a duration would park a
    /// client for fifty-odd years.
    /// </summary>
    [Fact]
    public void ResetIsAnAbsoluteInstantRatherThanADelay()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.Add("x-ratelimit-reset", "1755772800");

        var info = TamgaTransport.ReadRateLimitInfo(response);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1755772800), info.ResetAt);
    }

    /// <summary>
    /// Absent is not exhausted. The middleware returns before setting anything when the server has
    /// no rate limiter configured — <c>state.rate_limiter</c> is <c>None</c> whenever the Redis
    /// pool could not be built — so a client that read a missing <c>remaining</c> as <c>0</c> would
    /// throttle itself against a server that never limits it.
    /// </summary>
    [Fact]
    public void AResponseFromAnUnlimitedServerReadsAsAbsent_NotAsZeroRemaining()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);

        var info = TamgaTransport.ReadRateLimitInfo(response);

        Assert.False(info.IsPresent);
        Assert.Null(info.Limit);
        Assert.Null(info.Remaining);
        Assert.Null(info.Reset);
        Assert.Null(info.Window);
        Assert.Null(info.ResetAt);
    }

    /// <summary>
    /// A malformed value reads as absent rather than as a plausible-looking wrong number, and never
    /// throws: this is diagnostic metadata, same rule as <c>ReadResponseHeaders</c>.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-number")]
    [InlineData("+60")]
    [InlineData("-60")]
    [InlineData("60.5")]
    [InlineData("1,000")]
    [InlineData("99999999999999999999999")]
    public void AMalformedHeaderReadsAsAbsentRatherThanAsAWrongNumber(string raw)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", raw);

        Assert.Null(TamgaTransport.ReadRateLimitInfo(response).Remaining);
    }

    /// <summary>
    /// A partial set is normal: only some of the four need be present for the response to have come
    /// from a limited server, and the ones that are present must still be readable.
    /// </summary>
    [Fact]
    public void APartialHeaderSetStillCountsAsPresent()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("x-ratelimit-remaining", "0");

        var info = TamgaTransport.ReadRateLimitInfo(response);

        Assert.True(info.IsPresent);
        Assert.Equal(0, info.Remaining);
        Assert.Null(info.Limit);
    }

    /// <summary>
    /// <c>ResponseHeaders</c> stays exactly the shape it was. It is a positional record, so adding
    /// the rate-limit values to it would have changed its primary constructor and its
    /// <c>Deconstruct</c> signature — a break for every caller that constructs or deconstructs one.
    /// The four <c>x-ratelimit-*</c> values live on their own type and their own accessor instead.
    /// </summary>
    [Fact]
    public void TheRateLimitValuesAreNotBoltedOntoResponseHeaders()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Add("Tamga-Version", "1.8");
        response.Headers.Add("x-ratelimit-limit", "60");

        var headers = TamgaTransport.ReadResponseHeaders(response);

        Assert.Equal("1.8", headers.TamgaVersion);
        Assert.Equal(4, typeof(TamgaTransport.ResponseHeaders).GetConstructors().Single().GetParameters().Length);
        Assert.Null(typeof(TamgaTransport.ResponseHeaders).GetProperty("Limit"));
        Assert.Null(typeof(TamgaTransport.ResponseHeaders).GetProperty("Remaining"));
    }
}
