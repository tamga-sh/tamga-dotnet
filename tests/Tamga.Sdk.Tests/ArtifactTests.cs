using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Tamga.Sdk.Models;
using Tamga.Sdk.Tests.Support;
using Xunit;

namespace Tamga.Sdk.Tests;

/// <summary>
/// The release-artifact surface: the keyset listing, the metadata read, and the download action
/// that resolves a presigned storage URL.
/// </summary>
/// <remarks>
/// <para>
/// Every response fixture here is built from <c>tamga-api</c>'s own
/// <c>artifacts/serializer.rs</c>, not from what this SDK would like to receive. That matters
/// specifically: <c>ArtifactAttributes</c> is <c>rename_all = "camelCase"</c> AND carries explicit
/// <c>rename = "created"</c>/<c>"updated"</c>, so the wire names are <c>redirectUrl</c> but
/// <c>created</c>/<c>updated</c>. A fixture written with <c>createdAt</c>/<c>updatedAt</c> would
/// agree with the bug instead of catching it — which is exactly how the flat component/process
/// decode stayed green in CI for the SDK's whole life.
/// </para>
/// </remarks>
public class ArtifactTests
{
    private static (TamgaClient Client, MockHttpMessageHandler Handler) MakeClient(
        HttpClient? downloadHttpClient = null,
        AuthTransport? auth = null)
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var options = new TamgaClientOptions
        {
            AccountId = "acct-1",
            BaseUrl = "https://api.tamga.test",
            Auth = auth ?? new AuthTransport.License("LICENCE-KEY"),
            ArtifactDownloadHttpClient = downloadHttpClient,
        };
        return (new TamgaClient(options, httpClient), handler);
    }

    /// <summary>
    /// The exact attribute names <c>artifacts/serializer.rs</c> emits. <c>created</c>/<c>updated</c>
    /// are deliberately NOT <c>createdAt</c>/<c>updatedAt</c>.
    /// </summary>
    private static JsonObject ArtifactResource(Guid id, string? redirectUrl = null)
    {
        var attributes = new JsonObject
        {
            ["filename"] = "MyApp-1.2.3-win-x64.exe",
            ["filetype"] = "exe",
            ["filesize"] = 12_345_678L,
            ["checksum"] = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
            ["platform"] = "windows",
            ["arch"] = "x64",
            ["signature"] = "MEUCIQ...",
            ["status"] = "UPLOADED",
            ["metadata"] = new JsonObject { ["channel"] = "stable" },
            ["created"] = "2026-01-02T03:04:05Z",
            ["updated"] = "2026-01-02T03:04:06Z",
        };

        if (redirectUrl is not null)
        {
            attributes["redirectUrl"] = redirectUrl;
        }

        return new JsonObject
        {
            ["type"] = "artifacts",
            ["id"] = id.ToString(),
            ["attributes"] = attributes,
        };
    }

    private static string SingleBody(Guid id, string? redirectUrl = null) =>
        new JsonObject { ["data"] = ArtifactResource(id, redirectUrl) }.ToJsonString();

    private static string ListBody(params Guid[] ids)
    {
        var data = new JsonArray();
        foreach (var id in ids)
        {
            data.Add(ArtifactResource(id));
        }

        return new JsonObject { ["data"] = data }.ToJsonString();
    }

    // ── Envelope decoding ────────────────────────────────────────────────────

    /// <summary>
    /// <c>id</c> is a SIBLING of <c>attributes</c>, and the two timestamps are <c>created</c> /
    /// <c>updated</c>. Decoding straight into a model, or applying camelCase uniformly, produces a
    /// silently-blank object rather than an error.
    /// </summary>
    [Fact]
    public async Task GetArtifactAsync_DecodesTheEnvelope_IdFromTheSibling_AndTheUnsuffixedTimestamps()
    {
        var (client, handler) = MakeClient();
        var id = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, SingleBody(id));

        var artifact = await client.GetArtifactAsync(id);

        Assert.Equal(id, artifact.Id);
        Assert.NotEqual(Guid.Empty, artifact.Id);
        Assert.Equal("MyApp-1.2.3-win-x64.exe", artifact.Filename);
        Assert.Equal("exe", artifact.Filetype);
        Assert.Equal(12_345_678L, artifact.Filesize);
        Assert.Equal("windows", artifact.Platform);
        Assert.Equal("x64", artifact.Arch);
        Assert.Equal("MEUCIQ...", artifact.Signature);
        Assert.Equal("UPLOADED", artifact.Status);
        Assert.NotNull(artifact.Metadata);

        // The whole point of the two explicit server-side renames.
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T03:04:05Z"), artifact.Created);
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T03:04:06Z"), artifact.Updated);

        // Absent on show, per `skip_serializing_if = "Option::is_none"`.
        Assert.Null(artifact.RedirectUrl);

        var request = Assert.Single(handler.Requests).Request;
        Assert.Equal($"https://api.tamga.test/v1/accounts/acct-1/artifacts/{id}", request.RequestUri!.ToString());
    }

    /// <summary>
    /// A response using <c>createdAt</c>/<c>updatedAt</c> — what a uniform camelCase mapping would
    /// expect — must NOT bind. Pins the trap from the other side, so an implementation that
    /// "helpfully" accepts both spellings fails here.
    /// </summary>
    [Fact]
    public async Task GetArtifactAsync_DoesNotBindCreatedAtOrUpdatedAt()
    {
        var (client, handler) = MakeClient();
        var id = Guid.NewGuid();
        var body = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["type"] = "artifacts",
                ["id"] = id.ToString(),
                ["attributes"] = new JsonObject
                {
                    ["filename"] = "a.exe",
                    ["status"] = "UPLOADED",
                    ["createdAt"] = "2026-01-02T03:04:05Z",
                    ["updatedAt"] = "2026-01-02T03:04:06Z",
                },
            },
        }.ToJsonString();
        handler.Enqueue(HttpStatusCode.OK, body);

        var artifact = await client.GetArtifactAsync(id);

        Assert.Null(artifact.Created);
        Assert.Null(artifact.Updated);
    }

    // ── Listing ──────────────────────────────────────────────────────────────

    /// <summary>A short page ends the walk; the cursor stays null.</summary>
    [Fact]
    public async Task ListReleaseArtifactsAsync_SendsAnExplicitLimit_AndReturnsNoCursorOnAShortPage()
    {
        var (client, handler) = MakeClient();
        var releaseId = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, ListBody(Guid.NewGuid(), Guid.NewGuid()));

        var page = await client.ListReleaseArtifactsAsync(releaseId);

        Assert.Equal(2, page.Items.Count);
        Assert.Null(page.NextCursor);

        var uri = Assert.Single(handler.Requests).Request.RequestUri!;
        Assert.Equal($"/v1/accounts/acct-1/releases/{releaseId}/artifacts", uri.AbsolutePath);
        Assert.Contains("limit=100", uri.Query, StringComparison.Ordinal);
    }

    /// <summary>A full page synthesizes the cursor from the last row's id — the server sends no <c>links</c>.</summary>
    [Fact]
    public async Task ListReleaseArtifactsAsync_SynthesizesTheCursorFromTheLastIdOnAFullPage()
    {
        var (client, handler) = MakeClient();
        var releaseId = Guid.NewGuid();
        var first = Guid.NewGuid();
        var last = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, ListBody(first, last));

        var page = await client.ListReleaseArtifactsAsync(releaseId, limit: 2);

        Assert.Equal(last.ToString(), page.NextCursor);
    }

    /// <summary>The cursor is threaded back as <c>page[after]</c>, percent-encoded.</summary>
    [Fact]
    public async Task ListReleaseArtifactsAsync_SendsTheCursorAsPageAfter()
    {
        var (client, handler) = MakeClient();
        var releaseId = Guid.NewGuid();
        var cursor = Guid.NewGuid().ToString();
        handler.Enqueue(HttpStatusCode.OK, ListBody());

        await client.ListReleaseArtifactsAsync(releaseId, limit: 10, after: cursor);

        var query = Assert.Single(handler.Requests).Request.RequestUri!.Query;
        Assert.Contains("limit=10", query, StringComparison.Ordinal);
        Assert.Contains($"page%5Bafter%5D={cursor}", query, StringComparison.Ordinal);
    }

    /// <summary>Clamped to the server's own ceiling, so the fullness comparison is measured against the limit the server will honour.</summary>
    [Fact]
    public async Task ListReleaseArtifactsAsync_ClampsTheLimitToTheServersCeiling()
    {
        var (client, handler) = MakeClient();
        handler.Enqueue(HttpStatusCode.OK, ListBody());

        await client.ListReleaseArtifactsAsync(Guid.NewGuid(), limit: 5000);

        var query = Assert.Single(handler.Requests).Request.RequestUri!.Query;
        Assert.Contains("limit=100", query, StringComparison.Ordinal);
    }

    /// <summary>Zero would satisfy the fullness test against an empty page and then index into it.</summary>
    [Fact]
    public async Task ListReleaseArtifactsAsync_RejectsANonPositiveLimit()
    {
        var (client, _) = MakeClient();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.ListReleaseArtifactsAsync(Guid.NewGuid(), limit: 0));
    }

    // ── Download URL resolution ──────────────────────────────────────────────

    /// <summary>
    /// THE SECURITY CONTROL. <c>redirect=false</c> is sent on every download call, including when
    /// no TTL was asked for. Without it the server answers <c>303</c> to the storage host, and
    /// <see cref="HttpClientHandler.AllowAutoRedirect"/> defaults to <see langword="true"/>.
    /// </summary>
    [Fact]
    public async Task GetArtifactDownloadUrlAsync_AlwaysSendsRedirectFalse()
    {
        var (client, handler) = MakeClient();
        var id = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, SingleBody(id, "https://storage.test/blob?sig=abc"));

        await client.GetArtifactDownloadUrlAsync(id);

        var uri = Assert.Single(handler.Requests).Request.RequestUri!;
        Assert.Equal($"/v1/accounts/acct-1/artifacts/{id}/actions/download", uri.AbsolutePath);
        Assert.Contains("redirect=false", uri.Query, StringComparison.Ordinal);
    }

    /// <summary>The presigned URL comes back non-nullable, alongside the artifact it belongs to.</summary>
    [Fact]
    public async Task GetArtifactDownloadUrlAsync_ReturnsTheRedirectUrlAndTheArtifact()
    {
        var (client, handler) = MakeClient();
        var id = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, SingleBody(id, "https://storage.test/blob?sig=abc"));

        var download = await client.GetArtifactDownloadUrlAsync(id);

        Assert.Equal(new Uri("https://storage.test/blob?sig=abc"), download.Url);
        Assert.Equal(id, download.Artifact.Id);
        Assert.Equal("https://storage.test/blob?sig=abc", download.Artifact.RedirectUrl);
    }

    /// <summary>A whole-second TTL inside the server's range is sent as an integer count of seconds.</summary>
    [Fact]
    public async Task GetArtifactDownloadUrlAsync_SendsTheTtlInSeconds()
    {
        var (client, handler) = MakeClient();
        var id = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, SingleBody(id, "https://storage.test/blob"));

        await client.GetArtifactDownloadUrlAsync(id, TimeSpan.FromMinutes(30));

        var query = Assert.Single(handler.Requests).Request.RequestUri!.Query;
        Assert.Contains("ttl=1800", query, StringComparison.Ordinal);
        Assert.Contains("redirect=false", query, StringComparison.Ordinal);
    }

    /// <summary>The server validates [60s, 1 week]; catching it here names the call site that got it wrong.</summary>
    [Theory]
    [InlineData(59)]
    [InlineData(604_801)]
    [InlineData(0)]
    public async Task GetArtifactDownloadUrlAsync_RejectsATtlOutsideTheServersRange(int seconds)
    {
        var (client, _) = MakeClient();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.GetArtifactDownloadUrlAsync(Guid.NewGuid(), TimeSpan.FromSeconds(seconds)));
    }

    /// <summary>Both ends of the range are accepted, so the guard cannot be an off-by-one.</summary>
    [Theory]
    [InlineData(60)]
    [InlineData(604_800)]
    public async Task GetArtifactDownloadUrlAsync_AcceptsBothEndsOfTheRange(int seconds)
    {
        var (client, handler) = MakeClient();
        var id = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, SingleBody(id, "https://storage.test/blob"));

        await client.GetArtifactDownloadUrlAsync(id, TimeSpan.FromSeconds(seconds));

        Assert.Contains($"ttl={seconds}", Assert.Single(handler.Requests).Request.RequestUri!.Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// The server parses <c>ttl</c> as whole seconds, so a fractional <see cref="TimeSpan"/> would
    /// be truncated — and truncating 59.9s to 59 turns a nearly-valid request into a 422.
    /// </summary>
    [Fact]
    public async Task GetArtifactDownloadUrlAsync_RejectsAFractionalTtl()
    {
        var (client, _) = MakeClient();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.GetArtifactDownloadUrlAsync(Guid.NewGuid(), TimeSpan.FromMilliseconds(90_500)));
    }

    /// <summary>
    /// If <c>redirect=false</c> were dropped or rewritten en route, the resource comes back with no
    /// <c>redirectUrl</c>. Fail loudly rather than hand back a fabricated URL.
    /// </summary>
    [Fact]
    public async Task GetArtifactDownloadUrlAsync_ThrowsWhenTheServerReturnedNoRedirectUrl()
    {
        var (client, handler) = MakeClient();
        var id = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, SingleBody(id));

        var ex = await Assert.ThrowsAsync<TamgaApiException>(() => client.GetArtifactDownloadUrlAsync(id));

        Assert.Equal("MISSING_REDIRECT_URL", ex.Error.Code);
    }

    /// <summary>
    /// A redirectUrl that is not an absolute <b>http(s)</b> URI is a server/proxy fault, not a Uri
    /// parse to swallow.
    /// </summary>
    /// <remarks>
    /// <c>UriKind.Absolute</c> on its own is not the guard, and believing it is would be a
    /// three-OS bug. Measured on net8.0: <c>Uri.TryCreate("/relative/path", UriKind.Absolute, …)</c>
    /// returns <see langword="true"/> on Unix and yields a <c>file:</c> URI — so does
    /// <c>"C:\x\y"</c>. Only the scheme check refuses these, and it refuses them identically on
    /// ubuntu, macos and windows. <c>file:///etc/passwd</c> is in the theory for the same reason:
    /// a caller handed that back would have been handed a local-filesystem reference by a remote
    /// party.
    /// </remarks>
    [Theory]
    [InlineData("/relative/path")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not a uri")]
    [InlineData("ftp://storage.test/blob")]
    public async Task GetArtifactDownloadUrlAsync_ThrowsOnARedirectUrlThatIsNotAbsoluteHttp(string redirectUrl)
    {
        var (client, handler) = MakeClient();
        var id = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, SingleBody(id, redirectUrl));

        var ex = await Assert.ThrowsAsync<TamgaApiException>(() => client.GetArtifactDownloadUrlAsync(id));

        Assert.Equal("INVALID_REDIRECT_URL", ex.Error.Code);
    }

    /// <summary>Plain <c>http</c> is accepted — a self-hosted MinIO behind a private network is a real deployment.</summary>
    [Fact]
    public async Task GetArtifactDownloadUrlAsync_AcceptsPlainHttp()
    {
        var (client, handler) = MakeClient();
        var id = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, SingleBody(id, "http://minio.internal:9000/blob?sig=abc"));

        var download = await client.GetArtifactDownloadUrlAsync(id);

        Assert.Equal(new Uri("http://minio.internal:9000/blob?sig=abc"), download.Url);
    }

    /// <summary>
    /// A <c>403</c> on the download action is NOT necessarily a bad credential: the route runs the
    /// owning release's <c>enforce_release_access</c> gate as well as the permission check, so a
    /// CLOSED release refuses a caller that genuinely holds <c>artifact.download</c>. It must still
    /// surface as the ordinary typed exception.
    /// </summary>
    [Fact]
    public async Task GetArtifactDownloadUrlAsync_SurfacesAReleaseGateRefusalAsForbidden()
    {
        var (client, handler) = MakeClient();
        var body = new JsonObject
        {
            ["errors"] = new JsonArray
            {
                new JsonObject
                {
                    ["status"] = "403",
                    ["code"] = "FORBIDDEN",
                    ["detail"] = "You do not have permission to access this release",
                },
            },
        }.ToJsonString();
        handler.Enqueue(HttpStatusCode.Forbidden, body);

        var ex = await Assert.ThrowsAsync<TamgaForbiddenException>(
            () => client.GetArtifactDownloadUrlAsync(Guid.NewGuid()));

        Assert.Equal("FORBIDDEN", ex.Error.Code);
    }

    /// <summary>A server with no object storage answers 422 STORAGE_UNAVAILABLE — an ordinary mapped error.</summary>
    [Fact]
    public async Task GetArtifactDownloadUrlAsync_SurfacesStorageUnavailable()
    {
        var (client, handler) = MakeClient();
        var body = new JsonObject
        {
            ["errors"] = new JsonArray
            {
                new JsonObject
                {
                    ["status"] = "422",
                    ["code"] = "STORAGE_UNAVAILABLE",
                    ["detail"] = "No storage backend is configured, so artifacts cannot be downloaded",
                },
            },
        }.ToJsonString();
        handler.Enqueue(HttpStatusCode.UnprocessableEntity, body);

        var ex = await Assert.ThrowsAsync<TamgaApiException>(() => client.GetArtifactDownloadUrlAsync(Guid.NewGuid()));

        Assert.Equal("STORAGE_UNAVAILABLE", ex.Error.Code);
    }

    /// <summary>
    /// A server-side TTL rejection arrives as <c>PRESIGN_TTL_INVALID</c>, NOT the
    /// <c>TTL_INVALID</c> the two checkout routes use — so it does not map to
    /// <see cref="TtlInvalidException"/>.
    /// </summary>
    /// <remarks>
    /// Pinned because the near-identical name invites the assumption that the existing typed
    /// exception covers it. <c>artifacts/service.rs:33</c> emits <c>PRESIGN_TTL_INVALID</c>;
    /// <c>check_out_license.rs:48</c> and <c>check_out_machine.rs:50</c> emit <c>TTL_INVALID</c>;
    /// only the latter is in <c>TamgaErrorMapper</c>. Asserting the negative is the point.
    /// </remarks>
    [Fact]
    public async Task GetArtifactDownloadUrlAsync_SurfacesPresignTtlInvalid_WhichIsNotTtlInvalid()
    {
        var (client, handler) = MakeClient();
        var body = new JsonObject
        {
            ["errors"] = new JsonArray
            {
                new JsonObject
                {
                    ["status"] = "422",
                    ["code"] = "PRESIGN_TTL_INVALID",
                    ["detail"] = "Presigned URL TTL must be between 1 minute and 1 week",
                },
            },
        }.ToJsonString();
        handler.Enqueue(HttpStatusCode.UnprocessableEntity, body);

        var ex = await Assert.ThrowsAsync<TamgaApiException>(
            () => client.GetArtifactDownloadUrlAsync(Guid.NewGuid()));

        Assert.Equal("PRESIGN_TTL_INVALID", ex.Error.Code);
        Assert.IsNotType<TtlInvalidException>(ex);
    }

    // ── Byte fetch ───────────────────────────────────────────────────────────

    /// <summary>
    /// THE OTHER HALF OF THE SECURITY CONTROL. The storage fetch must carry no Tamga credential of
    /// any kind — no <c>Authorization</c>, no <c>Cookie</c>, nothing from the API request pipeline.
    /// </summary>
    /// <remarks>
    /// The <c>Cookie</c> assertion is the one that matters most and it is the least obvious.
    /// Measured on net8.0: .NET's redirect handler strips <c>Authorization</c> on an automatic
    /// redirect, but a <c>Cookie</c> header set directly on the request — which is exactly how
    /// <see cref="AuthTransport.Cookie"/> sets <c>Tamga-Session</c> — is forwarded verbatim,
    /// cross-origin. So the credential a followed <c>303</c> would actually hand to the storage
    /// host is the session, and this test is configured with that transport for that reason.
    /// </remarks>
    [Fact]
    public async Task DownloadArtifactAsync_SendsNoTamgaCredentialToTheStorageHost()
    {
        var storage = new MockHttpMessageHandler();
        storage.Enqueue(_ => MockHttpMessageHandler.MakeResponse(
            HttpStatusCode.OK, "BINARY-BYTES", "application/octet-stream"));

        var (client, handler) = MakeClient(
            downloadHttpClient: new HttpClient(storage),
            auth: new AuthTransport.Cookie("SECRET-SESSION", "https://app.tamga.test"));

        var id = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, SingleBody(id, "https://storage.test/blob?sig=abc"));

        using var destination = new MemoryStream();
        var download = await client.DownloadArtifactAsync(id, destination);

        Assert.Equal("BINARY-BYTES", Encoding.UTF8.GetString(destination.ToArray()));
        Assert.Equal(new Uri("https://storage.test/blob?sig=abc"), download.Url);

        // The API leg did carry the session cookie.
        var apiRequest = Assert.Single(handler.Requests).Request;
        Assert.True(apiRequest.Headers.Contains("Cookie"));

        // The storage leg carried nothing at all.
        var storageRequest = Assert.Single(storage.Requests).Request;
        Assert.Null(storageRequest.Headers.Authorization);
        Assert.False(storageRequest.Headers.Contains("Cookie"));
        Assert.False(storageRequest.Headers.Contains("Origin"));
        Assert.False(storageRequest.Headers.Contains("Tamga-Version"));
        Assert.Empty(storageRequest.Headers);
    }

    /// <summary>The two legs go to two different hosts, on two different clients.</summary>
    [Fact]
    public async Task DownloadArtifactAsync_UsesASeparateClientForTheStorageFetch()
    {
        var storage = new MockHttpMessageHandler();
        storage.Enqueue(_ => MockHttpMessageHandler.MakeResponse(HttpStatusCode.OK, "X", "application/octet-stream"));

        var (client, handler) = MakeClient(downloadHttpClient: new HttpClient(storage));
        var id = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, SingleBody(id, "https://storage.test/blob?sig=abc"));

        using var destination = new MemoryStream();
        await client.DownloadArtifactAsync(id, destination);

        Assert.Equal("api.tamga.test", Assert.Single(handler.Requests).Request.RequestUri!.Host);
        Assert.Equal("storage.test", Assert.Single(storage.Requests).Request.RequestUri!.Host);
    }

    /// <summary>A storage-side failure is an <see cref="HttpRequestException"/>: it carries no JSON:API envelope to map.</summary>
    [Fact]
    public async Task DownloadArtifactAsync_ThrowsWhenTheStorageHostRefuses()
    {
        var storage = new MockHttpMessageHandler();
        storage.Enqueue(_ => MockHttpMessageHandler.MakeResponse(
            HttpStatusCode.Forbidden, "<Error>AccessDenied</Error>", "application/xml"));

        var (client, handler) = MakeClient(downloadHttpClient: new HttpClient(storage));
        var id = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, SingleBody(id, "https://storage.test/blob?sig=expired"));

        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<HttpRequestException>(() => client.DownloadArtifactAsync(id, destination));
    }

    /// <summary>Guard clauses run before any request is made.</summary>
    [Fact]
    public async Task DownloadArtifactAsync_RejectsANullDestination()
    {
        var (client, _) = MakeClient();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.DownloadArtifactAsync(Guid.NewGuid(), null!));
    }

    /// <summary>A read-only destination would fail deep inside the copy, after the URL was resolved.</summary>
    [Fact]
    public async Task DownloadArtifactAsync_RejectsAnUnwritableDestination()
    {
        var (client, _) = MakeClient();
        using var readOnly = new MemoryStream(new byte[4], writable: false);

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.DownloadArtifactAsync(Guid.NewGuid(), readOnly));
    }

    /// <summary>A caller-supplied download client keeps caller ownership, exactly like the API client.</summary>
    [Fact]
    public async Task Dispose_DoesNotDisposeACallerSuppliedDownloadClient()
    {
        var storage = new MockHttpMessageHandler();
        storage.Enqueue(_ => MockHttpMessageHandler.MakeResponse(HttpStatusCode.OK, "X", "application/octet-stream"));
        var downloadClient = new HttpClient(storage);

        var (client, handler) = MakeClient(downloadHttpClient: downloadClient);
        var id = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, SingleBody(id, "https://storage.test/blob"));

        using (var destination = new MemoryStream())
        {
            await client.DownloadArtifactAsync(id, destination);
        }

        client.Dispose();

        // Still usable: disposing it would make this throw ObjectDisposedException.
        storage.Enqueue(_ => MockHttpMessageHandler.MakeResponse(HttpStatusCode.OK, "Y", "application/octet-stream"));
        var again = await downloadClient.GetStringAsync("https://storage.test/blob");
        Assert.Equal("Y", again);
    }

    /// <summary>One the SDK created for itself goes away with the client, and disposing twice is safe.</summary>
    [Fact]
    public void Dispose_IsSafeWithNoDownloadClientEverCreated()
    {
        var (client, _) = MakeClient();

        client.Dispose();
        client.Dispose();
    }

    /// <summary>
    /// With no <see cref="TamgaClientOptions.ArtifactDownloadHttpClient"/> supplied, the SDK
    /// creates ONE credential-free client, caches it, and never hands back the credential-bearing
    /// API client.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted against the seam directly rather than through a download, because every way of
    /// reaching it through <see cref="TamgaClient.DownloadArtifactAsync"/> needs a real socket and
    /// this project does not make live-network calls in tests.
    /// </para>
    /// <para>
    /// This replaced a version that pre-cancelled the <see cref="CancellationToken"/> and asserted
    /// <see cref="OperationCanceledException"/>. It passed — and never executed a line of the code
    /// it claimed to cover, because <see cref="HttpClient"/> observes the token inside
    /// <c>ReadAsStringAsync</c> on the FIRST leg, so the download client was never created. Line
    /// coverage on the lazy-creation block is what exposed it.
    /// </para>
    /// </remarks>
    [Fact]
    public void GetOrCreateArtifactDownloadHttpClient_CreatesOneCredentialFreeClientAndCachesIt()
    {
        var handler = new MockHttpMessageHandler();
        var apiHttpClient = new HttpClient(handler);
        using var client = new TamgaClient(
            new TamgaClientOptions
            {
                AccountId = "acct-1",
                BaseUrl = "https://api.tamga.test",
                Auth = new AuthTransport.License("LICENCE-KEY"),
            },
            apiHttpClient);

        var first = client.GetOrCreateArtifactDownloadHttpClient();
        var second = client.GetOrCreateArtifactDownloadHttpClient();

        Assert.Same(first, second);
        Assert.NotSame(apiHttpClient, first);
        Assert.Null(first.DefaultRequestHeaders.Authorization);
        Assert.Empty(first.DefaultRequestHeaders);
    }

    /// <summary>A supplied client is handed straight back, and no second one is created behind it.</summary>
    [Fact]
    public void GetOrCreateArtifactDownloadHttpClient_ReturnsTheSuppliedClientUnchanged()
    {
        using var supplied = new HttpClient(new MockHttpMessageHandler());
        var (client, _) = MakeClient(downloadHttpClient: supplied);

        Assert.Same(supplied, client.GetOrCreateArtifactDownloadHttpClient());

        client.Dispose();

        // Still usable — Dispose must not have touched a client it does not own.
        Assert.Equal(TimeSpan.FromSeconds(100), supplied.Timeout);
    }

    /// <summary>An SDK-created client IS disposed with the client that created it, and twice is safe.</summary>
    [Fact]
    public async Task Dispose_DisposesADownloadClientTheSdkCreatedItself()
    {
        var (client, _) = MakeClient();

        var owned = client.GetOrCreateArtifactDownloadHttpClient();

        client.Dispose();
        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => owned.GetStringAsync("https://storage.test/blob"));
    }

    /// <summary>
    /// A <c>200</c> whose envelope carries <c>data: null</c> is not a resource. Both read paths
    /// must say so rather than map a default-constructed artifact.
    /// </summary>
    [Fact]
    public async Task GetArtifactAsync_ThrowsWhenTheEnvelopeCarriesNoData()
    {
        var (client, handler) = MakeClient();
        handler.Enqueue(HttpStatusCode.OK, new JsonObject { ["data"] = null }.ToJsonString());

        var ex = await Assert.ThrowsAsync<TamgaApiException>(() => client.GetArtifactAsync(Guid.NewGuid()));

        Assert.Equal("MISSING_DATA", ex.Error.Code);
    }

    /// <summary>The download action, same rule.</summary>
    [Fact]
    public async Task GetArtifactDownloadUrlAsync_ThrowsWhenTheEnvelopeCarriesNoData()
    {
        var (client, handler) = MakeClient();
        handler.Enqueue(HttpStatusCode.OK, new JsonObject { ["data"] = null }.ToJsonString());

        var ex = await Assert.ThrowsAsync<TamgaApiException>(
            () => client.GetArtifactDownloadUrlAsync(Guid.NewGuid()));

        Assert.Equal("MISSING_DATA", ex.Error.Code);
    }
}
