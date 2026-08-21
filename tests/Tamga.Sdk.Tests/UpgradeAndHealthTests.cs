using System.Net;
using System.Text.Json.Nodes;
using System.Web;
using Tamga.Sdk.Models;
using Tamga.Sdk.Tests.Support;
using Xunit;

namespace Tamga.Sdk.Tests;

/// <summary>
/// The two routes that sit outside the ordinary licensing surface: the auto-updater's upgrade check
/// (whose <c>204</c> means two different things) and <c>GET /v1/health</c> (the one call that is
/// not account-scoped).
/// </summary>
public class UpgradeAndHealthTests
{
    private static (TamgaClient Client, MockHttpMessageHandler Handler) MakeClient(AuthTransport? auth = null)
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var options = new TamgaClientOptions
        {
            AccountId = "acct-1",
            BaseUrl = "https://api.tamga.test",
            Auth = auth,
        };
        return (new TamgaClient(options, httpClient), handler);
    }

    private static UpgradeCheckRequest Request(Guid productId) => new()
    {
        ProductId = productId,
        Platform = "windows",
        Filetype = "exe",
        Version = "1.2.3",
    };

    [Fact]
    public async Task CheckForUpgradeAsync_SendsTheFourRequiredParameters()
    {
        var (client, handler) = MakeClient();
        var productId = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.NoContent, "");

        await client.CheckForUpgradeAsync(Request(productId));

        var uri = handler.Requests[0].Request.RequestUri!;
        Assert.Equal("/v1/accounts/acct-1/releases/actions/upgrade", uri.AbsolutePath);

        var query = HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal(productId.ToString(), query["product"]);
        Assert.Equal("windows", query["platform"]);
        Assert.Equal("exe", query["filetype"]);
        Assert.Equal("1.2.3", query["version"]);
        // Both optional parameters must be omitted rather than sent empty: an empty `channel` is
        // not the same request as no `channel`, and an empty `constraint` is not valid semver.
        Assert.Null(query["channel"]);
        Assert.Null(query["constraint"]);
    }

    [Fact]
    public async Task CheckForUpgradeAsync_SendsTheOptionalParametersWhenGiven()
    {
        var (client, handler) = MakeClient();
        handler.Enqueue(HttpStatusCode.NoContent, "");

        await client.CheckForUpgradeAsync(Request(Guid.NewGuid()) with { Channel = "stable", Constraint = "^1.0.0" });

        var query = HttpUtility.ParseQueryString(handler.Requests[0].Request.RequestUri!.Query);
        Assert.Equal("stable", query["channel"]);
        Assert.Equal("^1.0.0", query["constraint"]);
    }

    /// <summary>
    /// The release resource is the only one in the API whose attributes are camelCase, and
    /// <c>created</c>/<c>updated</c> are the exceptions to that exception. Getting either wrong
    /// silently produces a release with a default product id or missing timestamps.
    /// </summary>
    [Fact]
    public async Task CheckForUpgradeAsync_DecodesTheCamelCaseReleaseResource()
    {
        var (client, handler) = MakeClient();
        var releaseId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var body = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["type"] = "releases",
                ["id"] = releaseId.ToString(),
                ["attributes"] = new JsonObject
                {
                    ["productId"] = productId.ToString(),
                    ["name"] = "1.3.0",
                    ["version"] = "1.3.0",
                    ["channel"] = "stable",
                    ["status"] = "PUBLISHED",
                    ["tag"] = "v1.3.0",
                    ["metadata"] = new JsonObject { ["notes"] = "…" },
                    ["created"] = "2026-02-01T00:00:00Z",
                    ["updated"] = "2026-02-02T00:00:00Z",
                },
            },
        }.ToJsonString();
        handler.Enqueue(HttpStatusCode.OK, body);

        var result = await client.CheckForUpgradeAsync(Request(productId));

        Assert.True(result.UpgradeOffered);
        Assert.Equal(releaseId, result.Release!.Id);
        Assert.Equal(productId, result.Release.ProductId);
        Assert.Equal("1.3.0", result.Release.Version);
        Assert.Equal("stable", result.Release.Channel);
        Assert.Equal("PUBLISHED", result.Release.Status);
        Assert.Equal("v1.3.0", result.Release.Tag);
        Assert.NotNull(result.Release.Metadata);
        Assert.NotNull(result.Release.Created);
        Assert.NotNull(result.Release.Updated);
    }

    /// <summary>
    /// <c>204</c> is not an error and must not become one — but it is also not "you are up to
    /// date". The server answers it both when nothing newer exists and when something newer exists
    /// that this licence may not have, deliberately, so that a refusal cannot leak the existence of
    /// a build. All this SDK may report is that no release was offered.
    /// </summary>
    [Fact]
    public async Task CheckForUpgradeAsync_TreatsNoContentAsNoOffer_NotAsAnError()
    {
        var (client, handler) = MakeClient();
        handler.Enqueue(HttpStatusCode.NoContent, "");

        var result = await client.CheckForUpgradeAsync(Request(Guid.NewGuid()));

        Assert.False(result.UpgradeOffered);
        Assert.Null(result.Release);
    }

    /// <summary>A suspended licence is the one nearby case that is NOT silent.</summary>
    [Fact]
    public async Task CheckForUpgradeAsync_SurfacesTheSuspendedLicence403()
    {
        var (client, handler) = MakeClient();
        const string body = """
        {"errors":[{"id":"1","status":"403","code":"FORBIDDEN","title":"Forbidden","detail":"The license is suspended and does not have access to this release"}]}
        """;
        handler.Enqueue(HttpStatusCode.Forbidden, body);

        var ex = await Assert.ThrowsAsync<TamgaForbiddenException>(() =>
            client.CheckForUpgradeAsync(Request(Guid.NewGuid())));

        Assert.Contains("suspended", ex.Error.Detail);
    }

    [Fact]
    public async Task CheckForUpgradeAsync_KeepsTheServersCodeOnAnInvalidVersion()
    {
        var (client, handler) = MakeClient();
        const string body = """
        {"errors":[{"id":"1","status":"422","code":"INVALID_VERSION","title":"Unprocessable Entity","detail":"not semver"}]}
        """;
        handler.Enqueue(HttpStatusCode.UnprocessableEntity, body);

        var ex = await Assert.ThrowsAsync<TamgaApiException>(() =>
            client.CheckForUpgradeAsync(Request(Guid.NewGuid()) with { Version = "not-semver" }));

        Assert.Equal("INVALID_VERSION", ex.Error.Code);
    }

    /// <summary>
    /// A missing required query parameter is rejected by the server's own query extractor before
    /// any handler runs, so the body is plain text rather than a JSON:API error envelope. The
    /// SDK's required-parameter typing means a caller cannot reach that path, but the error
    /// recovery must still degrade cleanly rather than crash on the unparseable body.
    /// </summary>
    [Fact]
    public async Task CheckForUpgradeAsync_DegradesCleanlyOnAPlainTextRejection()
    {
        var (client, handler) = MakeClient();
        handler.Enqueue(HttpStatusCode.BadRequest, "Failed to deserialize query string", contentType: "text/plain");

        var ex = await Assert.ThrowsAsync<TamgaApiException>(() =>
            client.CheckForUpgradeAsync(Request(Guid.NewGuid())));

        Assert.Equal("UNPARSEABLE_ERROR_BODY", ex.Error.Code);
        Assert.Equal(400, ex.Error.Status);
    }

    // ── /v1/health ────────────────────────────────────────────────────────────

    /// <summary>
    /// The reason this route was unreachable was never the server — it was that every URL this SDK
    /// builds gets an unconditional <c>/v1/accounts/{account_id}</c> prefix. Pinning the absolute
    /// path is the whole test.
    /// </summary>
    [Fact]
    public async Task GetHealthAsync_SkipsTheAccountPrefix()
    {
        var (client, handler) = MakeClient();
        handler.Enqueue(HttpStatusCode.OK, """{"status":"ok","version":"1.9.0","uptime_secs":4321}""", contentType: "application/json");

        var health = await client.GetHealthAsync();

        var uri = handler.Requests[0].Request.RequestUri!;
        Assert.Equal("/v1/health", uri.AbsolutePath);
        Assert.DoesNotContain("accounts", uri.AbsolutePath);
        Assert.Equal("https://api.tamga.test", uri.GetLeftPart(UriPartial.Authority));

        Assert.Equal("ok", health.Status);
        Assert.Equal("1.9.0", health.Version);
        Assert.Equal(4321, health.UptimeSeconds);
    }

    /// <summary>
    /// The health handler returns a bare object — no <c>data</c>, no <c>type</c>, no
    /// <c>attributes</c>. Sending it through the envelope decoder yields nothing, which is why it
    /// has its own path.
    /// </summary>
    [Fact]
    public async Task GetHealthAsync_DecodesAFlatBody_NotAJsonApiEnvelope()
    {
        var (client, handler) = MakeClient();
        handler.Enqueue(HttpStatusCode.OK, """{"status":"ok","version":"2.0.0","uptime_secs":1}""", contentType: "application/json");

        var health = await client.GetHealthAsync();

        Assert.Equal("2.0.0", health.Version);
        Assert.Equal(1, health.UptimeSeconds);
    }

    /// <summary>
    /// The credential still goes out — the route ignores it, and sending it keeps callers
    /// forward-compatible if that changes. It also keeps this SDK's "exactly one configured
    /// transport, always sent" rule from acquiring an exception.
    /// </summary>
    [Fact]
    public async Task GetHealthAsync_StillSendsTheConfiguredCredential()
    {
        var (client, handler) = MakeClient(new AuthTransport.License("LIC-KEY"));
        handler.Enqueue(HttpStatusCode.OK, """{"status":"ok","version":"1","uptime_secs":0}""", contentType: "application/json");

        await client.GetHealthAsync();

        var auth = handler.Requests[0].Request.Headers.Authorization;
        Assert.Equal("License", auth!.Scheme);
        Assert.Equal("LIC-KEY", auth.Parameter);
    }

    [Fact]
    public async Task GetHealthAsync_MapsAnErrorStatusLikeEveryOtherCall()
    {
        var (client, handler) = MakeClient();
        const string body = """{"errors":[{"id":"1","status":"403","code":"FORBIDDEN","title":"Forbidden","detail":"The Host header does not match any configured host"}]}""";
        handler.Enqueue(HttpStatusCode.Forbidden, body);

        var ex = await Assert.ThrowsAsync<TamgaForbiddenException>(() => client.GetHealthAsync());
        Assert.Contains("Host header", ex.Error.Detail);
    }
}
