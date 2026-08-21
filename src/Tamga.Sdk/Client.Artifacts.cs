using Tamga.Sdk.Models;

namespace Tamga.Sdk;

public sealed partial class TamgaClient
{
    // ---------------------------------------------------------------
    // §M Release artifacts
    //
    // Fully reachable with a licence key since tamga-api e6d317b ("grant artifact.download
    // explicitly and gate the download route").
    //
    // Be precise about what that commit changed, because the two halves have different histories.
    // It added exactly ONE permission per role: `artifact.download` (shared/authz/mod.rs:265, and
    // the same single line for Role::Developer). `artifact.read` was ALREADY on
    // `Role::LicenseToken` before it (:264) -- so listing artifacts and reading their metadata was
    // never the blocked half, and this SDK could have exposed those two routes at any point. Only
    // fetching the bytes was refused, because `artifact.download` appeared in no role's default
    // list at all and `effective_permissions` intersects the bearer and token sets, so granting it
    // on a token could not recover it either.
    //
    // Read-only, and that is a server fact rather than a scoping decision here:
    // `artifact.create`/`update`/`delete` are absent from `Role::LicenseToken`, and
    // `ArtifactPolicy::can_create/update/delete` additionally require Admin/Developer/ProductToken/
    // EnvironmentToken (artifacts/policy.rs). Create, update, delete and upload are therefore out
    // of scope for this SDK — do not add them expecting a licence key to reach them.
    // ---------------------------------------------------------------

    /// <summary>The server's maximum (and this SDK's default) page size for artifact listings — <c>limit</c> is clamped to <c>1..100</c> server-side.</summary>
    private const int MaxArtifactsPageSize = 100;

    /// <summary>
    /// Shortest presigned-URL lifetime the server accepts, in seconds — <c>PRESIGN_TTL_MIN</c>
    /// (<c>artifacts/service.rs:15</c>). A shorter TTL answers <c>422</c>.
    /// </summary>
    public const int MinDownloadTtlSeconds = 60;

    /// <summary>
    /// Longest presigned-URL lifetime the server accepts, in seconds (one week) —
    /// <c>PRESIGN_TTL_MAX</c> (<c>artifacts/service.rs:17</c>). A longer TTL answers <c>422</c>.
    /// </summary>
    public const int MaxDownloadTtlSeconds = 604_800;

    private HttpClient? _artifactDownloadHttpClient;
    private bool _ownsArtifactDownloadHttpClient;
    private readonly object _artifactDownloadClientLock = new();

    /// <summary>
    /// <c>GET /releases/{release_id}/artifacts</c> — keyset-paginated
    /// (<c>limit</c>/<c>page[after]</c>) list of a release's artifacts.
    /// </summary>
    /// <param name="releaseId">The release whose artifacts to list.</param>
    /// <param name="limit">Page size, <c>1..100</c>. Defaults to 100 (the maximum) rather than letting the server apply its silent default of 25. Values above 100 are clamped to match the server's own clamp; values below 1 are rejected.</param>
    /// <param name="after">The <c>page[after]</c> cursor from a previous page's <see cref="Page{T}.NextCursor"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// <para>
    /// The cursor works here, as it does for components and unlike entitlements: the server orders
    /// by <c>(created_at, id)</c> and seeds the keyset from the row named by <c>page[after]</c>
    /// (<c>artifacts/queries.rs:38-51</c>). It is synthesized from the last item's id on a full
    /// page, because the server emits no <c>links</c> object on any route to read one out of.
    /// </para>
    /// <para>
    /// This listing needs only <c>artifact.read</c>. It does <b>not</b> run the owning release's
    /// access gate — <c>list_artifacts</c> calls <c>ArtifactPolicy.require_read</c> and nothing
    /// else — so a CLOSED release's artifact metadata is listable by a caller who cannot download
    /// its bytes. Do not infer downloadability from a successful listing; see
    /// <see cref="GetArtifactDownloadUrlAsync"/>.
    /// </para>
    /// <para>
    /// <see cref="Artifact.RedirectUrl"/> is <see langword="null"/> on every row here. It is
    /// populated only by the download action.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is less than 1.</exception>
    /// <exception cref="TamgaForbiddenException"><c>403 FORBIDDEN</c> — the credential does not hold <c>artifact.read</c>.</exception>
    public async Task<Page<Artifact>> ListReleaseArtifactsAsync(
        Guid releaseId,
        int? limit = null,
        string? after = null,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit), limit, "Page size must be at least 1; pass null for the default of 100.");
        }

        var effectiveLimit = Math.Min(limit ?? MaxArtifactsPageSize, MaxArtifactsPageSize);
        var query = BuildPaginationQuery(effectiveLimit, after);

        var doc = await _transport.SendJsonApiListAsync<ArtifactAttributes>(
            HttpMethod.Get,
            $"/releases/{releaseId}/artifacts",
            query: query,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var items = doc.Data.Select(Artifact.FromResource).ToList();
        return new Page<Artifact>
        {
            Items = items,
            NextCursor = SynthesizeCursor(items.Count, effectiveLimit, items.Count > 0 ? items[^1].Id : Guid.Empty),
        };
    }

    /// <summary>
    /// <c>GET /artifacts/{artifact_id}</c> — reads one artifact's metadata. Does not produce a
    /// download URL; <see cref="Artifact.RedirectUrl"/> stays <see langword="null"/>.
    /// </summary>
    /// <param name="artifactId">The artifact to read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Like the listing, this needs only <c>artifact.read</c> and does not apply the owning
    /// release's access gate. Succeeding here says nothing about whether
    /// <see cref="GetArtifactDownloadUrlAsync"/> will.
    /// </remarks>
    /// <exception cref="TamgaNotFoundException"><c>404 NOT_FOUND</c> — no such artifact in this account.</exception>
    /// <exception cref="TamgaForbiddenException"><c>403 FORBIDDEN</c> — the credential does not hold <c>artifact.read</c>.</exception>
    public async Task<Artifact> GetArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        var doc = await _transport.SendJsonApiAsync<ArtifactAttributes>(
            HttpMethod.Get,
            $"/artifacts/{artifactId}",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return Artifact.FromResource(doc.Data ?? throw MissingDataError());
    }

    /// <summary>
    /// <c>GET /artifacts/{artifact_id}/actions/download</c> — resolves a short-lived presigned
    /// storage URL for the artifact's bytes, WITHOUT fetching them.
    /// </summary>
    /// <param name="artifactId">The artifact to resolve a download URL for.</param>
    /// <param name="ttl">
    /// How long the presigned URL should stay valid. Must be a whole number of seconds in
    /// <see cref="MinDownloadTtlSeconds"/>..<see cref="MaxDownloadTtlSeconds"/> (1 minute to 1
    /// week). <see langword="null"/> leaves the server to apply its own default.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The artifact's metadata plus the presigned <see cref="ArtifactDownload.Url"/>.</returns>
    /// <remarks>
    /// <para>
    /// ⚠ <b>This SDK always sends <c>?redirect=false</c>, and that is a security control, not a
    /// preference.</b> Left to itself the route answers <c>303 See Other</c> pointing at the
    /// storage host. <see cref="HttpClientHandler.AllowAutoRedirect"/> defaults to
    /// <see langword="true"/> and the client this SDK builds for itself
    /// (<c>new HttpClient { Timeout = … }</c>) takes that default, so a <c>303</c> would be
    /// followed automatically, off-origin, by the same client that carries the Tamga credential.
    /// </para>
    /// <para>
    /// Measured on net8.0 rather than assumed, both hops, against a control request that proves
    /// the credentials were present to begin with — because the obvious version of the claim is
    /// wrong and .NET does not behave like the Fetch standard here:
    /// </para>
    /// <list type="table">
    /// <listheader><term>hop</term><description>Authorization / Cookie</description></listheader>
    /// <item><term>no redirect</term><description>sent / sent</description></item>
    /// <item><term>same-origin <c>303</c></term><description><b>stripped</b> / <b>forwarded</b></description></item>
    /// <item><term>cross-origin <c>303</c></term><description><b>stripped</b> / <b>forwarded</b></description></item>
    /// </list>
    /// <para>
    /// So <see cref="HttpClient"/> drops <c>Authorization</c> on <em>every</em> automatic
    /// redirect, same-origin included — stricter than <c>fetch</c>, which keeps it within an
    /// origin — and a licence key therefore does <em>not</em> reach the storage host this way.
    /// What it does NOT drop is a <c>Cookie</c> header set directly on the request, and
    /// <see cref="AuthTransport.Cookie"/> sets exactly that (<c>Cookie: Tamga-Session=…</c>, added
    /// by hand rather than through a <see cref="System.Net.CookieContainer"/>). That one is
    /// forwarded verbatim on both hops. So the leak is real, it is the session credential rather
    /// than the licence key, and no handler default protects against it. Do not carry this table
    /// over to another Tamga SDK — the sibling ports measured different answers on their own
    /// runtimes.
    /// </para>
    /// <para>
    /// There is a second reason not to follow the redirect that holds whatever the credentials do:
    /// this SDK reads a JSON:API response with <c>ReadAsStringAsync</c>. A followed <c>303</c>
    /// would therefore pull the entire artifact into a single <see cref="string"/> and then try to
    /// parse it as JSON. Artifacts are installers; that is an out-of-memory failure, not a parse
    /// error.
    /// </para>
    /// <para>
    /// Asking for <c>redirect=false</c> sidesteps the whole question: the server returns the
    /// ordinary artifact resource with <c>redirectUrl</c> populated, no redirect is ever issued,
    /// and the credential-bearing request ends at the Tamga API. Fetch
    /// <see cref="ArtifactDownload.Url"/> with something that sends no Tamga credential —
    /// <see cref="DownloadArtifactAsync"/> does this for you.
    /// </para>
    /// <para>
    /// <b>A <c>403</c> here is not necessarily an auth misconfiguration.</b> Unlike the listing
    /// and the metadata read, this route enforces the owning release's read gate as well as the
    /// permission: it loads the release and runs
    /// <c>releases::service::enforce_release_access</c> — distribution strategy, suspension,
    /// expiry, entitlement — so a CLOSED release's binary is refused even to a caller that
    /// genuinely holds <c>artifact.download</c>. Before treating a <c>403</c> as a bad credential,
    /// check the release. The gate was added in the same commit that granted the permission,
    /// precisely because granting it alone would have let a licence key pull a CLOSED release's
    /// binary by asking for the payload instead of the release.
    /// </para>
    /// <para>
    /// ⚠ The returned URL is itself a bearer credential for those bytes until it expires. Do not
    /// log it or persist it.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ttl"/> is outside <see cref="MinDownloadTtlSeconds"/>..<see cref="MaxDownloadTtlSeconds"/>, or is not a whole number of seconds.</exception>
    /// <exception cref="TamgaNotFoundException"><c>404 NOT_FOUND</c> — no such artifact, or its release is gone.</exception>
    /// <exception cref="TamgaForbiddenException"><c>403 FORBIDDEN</c> — no <c>artifact.download</c>, OR the owning release's access gate refused. See the remarks.</exception>
    /// <exception cref="TamgaApiException">
    /// <c>422 STORAGE_UNAVAILABLE</c> when the server has no object-storage backend configured.
    /// Also <c>422 PRESIGN_TTL_INVALID</c> on a TTL the server rejects — note the code:
    /// <c>artifacts/service.rs:33</c> emits <c>PRESIGN_TTL_INVALID</c>, NOT the <c>TTL_INVALID</c>
    /// the two checkout routes use, so it does <b>not</b> map to
    /// <see cref="TtlInvalidException"/> and arrives as the base type. Match on
    /// <see cref="TamgaApiError.Code"/>. In practice the <paramref name="ttl"/> guard above should
    /// mean a correct caller never sees it.
    /// </exception>
    public async Task<ArtifactDownload> GetArtifactDownloadUrlAsync(
        Guid artifactId,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        var query = BuildDownloadQuery(ttl);

        var doc = await _transport.SendJsonApiAsync<ArtifactAttributes>(
            HttpMethod.Get,
            $"/artifacts/{artifactId}/actions/download",
            query: query,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var artifact = Artifact.FromResource(doc.Data ?? throw MissingDataError());

        // `redirectUrl` is `skip_serializing_if = "Option::is_none"` server-side, so its absence is
        // a real wire state rather than a decode failure — and the one way to reach it is for the
        // `redirect=false` we always send to have been dropped or overridden en route. Fail loudly:
        // the alternative is handing back an ArtifactDownload whose Url is a fabricated default.
        if (string.IsNullOrEmpty(artifact.RedirectUrl))
        {
            throw new TamgaApiException(new TamgaApiError
            {
                Status = 200,
                Code = "MISSING_REDIRECT_URL",
                Detail = "The download action returned no redirectUrl. This SDK always sends ?redirect=false, "
                       + "so the parameter was dropped or rewritten before the server saw it.",
            });
        }

        // The scheme check is NOT belt-and-braces on top of `UriKind.Absolute` — it is the actual
        // guard, and dropping it would be both a platform bug and a hazard. Measured on net8.0:
        // `Uri.TryCreate("/relative/path", UriKind.Absolute, out _)` returns TRUE on Unix, yielding
        // a `file:` URI, and so does `"C:\\x\\y"`. So "absolute" alone lets a malformed or hostile
        // redirectUrl through as a local-filesystem reference, and does it differently on the three
        // OSes this SDK is tested on. A presigned storage URL is always http(s); anything else is a
        // server or proxy fault and is refused here rather than handed to the caller.
        if (!Uri.TryCreate(artifact.RedirectUrl, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            throw new TamgaApiException(new TamgaApiError
            {
                Status = 200,
                Code = "INVALID_REDIRECT_URL",
                Detail = "The download action returned a redirectUrl that is not an absolute http(s) URI.",
            });
        }

        return new ArtifactDownload { Artifact = artifact, Url = url };
    }

    /// <summary>
    /// Resolves a presigned URL with <see cref="GetArtifactDownloadUrlAsync"/> and streams the
    /// artifact's bytes into <paramref name="destination"/>, sending NO Tamga credential to the
    /// storage host.
    /// </summary>
    /// <param name="artifactId">The artifact whose bytes to fetch.</param>
    /// <param name="destination">The stream the bytes are copied into. Not flushed, positioned or disposed by this method.</param>
    /// <param name="ttl">Presigned-URL lifetime — see <see cref="GetArtifactDownloadUrlAsync"/>.</param>
    /// <param name="cancellationToken">Cancels both requests and the copy.</param>
    /// <returns>The artifact's metadata and the URL the bytes came from.</returns>
    /// <remarks>
    /// <para>
    /// Two requests, on two different clients, deliberately. The first carries the Tamga
    /// credential and goes to the Tamga API; the second carries nothing and goes to object
    /// storage. The storage fetch uses
    /// <see cref="TamgaClientOptions.ArtifactDownloadHttpClient"/> when one is supplied, and
    /// otherwise a credential-free <see cref="HttpClient"/> this instance creates on first use and
    /// disposes with itself. The API client is never reused for it — see
    /// <see cref="GetArtifactDownloadUrlAsync"/> for what a shared client would forward.
    /// </para>
    /// <para>
    /// The body is streamed rather than buffered, so an artifact larger than memory is fine, but
    /// note the internally-created client uses <see cref="HttpClient"/>'s own default timeout
    /// (100s) rather than <see cref="TamgaClientOptions.Timeout"/> — the API timeout is sized for
    /// a JSON round trip, not a multi-gigabyte transfer. Supply
    /// <see cref="TamgaClientOptions.ArtifactDownloadHttpClient"/> to choose your own.
    /// </para>
    /// <para>
    /// ⚠ <b>This method does not verify <see cref="Artifact.Checksum"/>.</b> It cannot: the
    /// checksum's algorithm and encoding are inferred server-side from the string's shape, the
    /// publisher may have supplied none at all, and a verification that silently passes on a null
    /// checksum is worse than no verification. Hash what you received and compare it yourself
    /// before executing anything.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is not writable.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ttl"/> is out of range — see <see cref="GetArtifactDownloadUrlAsync"/>.</exception>
    /// <exception cref="HttpRequestException">The storage host refused or failed the fetch. Not mapped to a <see cref="TamgaApiException"/>: it is not a Tamga API response and carries no JSON:API error envelope.</exception>
    public async Task<ArtifactDownload> DownloadArtifactAsync(
        Guid artifactId,
        Stream destination,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("Destination stream is not writable.", nameof(destination));
        }

        var download = await GetArtifactDownloadUrlAsync(artifactId, ttl, cancellationToken).ConfigureAwait(false);

        var client = GetOrCreateArtifactDownloadHttpClient();

        // A bare HttpRequestMessage with no headers whatsoever. Nothing from ApplyAuth, nothing
        // from the Tamga request pipeline: the presigned signature in the query string is the only
        // credential this host is entitled to see.
        using var request = new HttpRequestMessage(HttpMethod.Get, download.Url);
        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);

        return download;
    }

    /// <summary>
    /// Builds the download action's query string: always <c>redirect=false</c>, plus <c>ttl</c>
    /// when one was asked for.
    /// </summary>
    /// <remarks>
    /// The TTL bound is checked here rather than left to the server's <c>422</c> so the mistake
    /// surfaces as an <see cref="ArgumentOutOfRangeException"/> at the call site that made it.
    /// Whole seconds only: the server parses <c>ttl</c> as an integer number of seconds
    /// (<c>Option&lt;u64&gt;</c>), so a fractional <see cref="TimeSpan"/> would be silently
    /// truncated — and truncating 59.9s to 59 turns a nearly-valid request into a rejected one.
    /// </remarks>
    private static string BuildDownloadQuery(TimeSpan? ttl)
    {
        // Sent unconditionally, including when the caller asked for no particular TTL. This is the
        // parameter that stops the server issuing a 303 to the storage host.
        var parts = new List<string> { "redirect=false" };

        if (ttl is not { } value)
        {
            return string.Join('&', parts);
        }

        if (value.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ttl), value, "Presigned-URL TTL must be a whole number of seconds.");
        }

        var seconds = (long)value.TotalSeconds;
        if (seconds < MinDownloadTtlSeconds || seconds > MaxDownloadTtlSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ttl),
                value,
                $"Presigned-URL TTL must be between {MinDownloadTtlSeconds} and {MaxDownloadTtlSeconds} seconds (1 minute to 1 week).");
        }

        parts.Add($"ttl={seconds}");
        return string.Join('&', parts);
    }

    /// <summary>
    /// The client the storage fetch goes out on: the caller's when one was supplied, otherwise a
    /// credential-free one created once and owned by this instance.
    /// </summary>
    /// <remarks>
    /// <c>internal</c> rather than <c>private</c> so the ownership contract can be asserted
    /// without a socket — see the <c>InternalsVisibleTo</c> note in <c>Tamga.Sdk.csproj</c>.
    /// </remarks>
    internal HttpClient GetOrCreateArtifactDownloadHttpClient()
    {
        if (Options.ArtifactDownloadHttpClient is { } supplied)
        {
            return supplied;
        }

        lock (_artifactDownloadClientLock)
        {
            if (_artifactDownloadHttpClient is null)
            {
                // Its own client, never the API one. `UseCookies = false` so no CookieContainer
                // this process shares can attach anything, and no default header is set on it at
                // all — the point of this object is that it has nothing to leak.
                _artifactDownloadHttpClient = new HttpClient(new HttpClientHandler { UseCookies = false });
                _ownsArtifactDownloadHttpClient = true;
            }

            return _artifactDownloadHttpClient;
        }
    }

    private void DisposeArtifactDownloadHttpClient()
    {
        lock (_artifactDownloadClientLock)
        {
            if (_ownsArtifactDownloadHttpClient)
            {
                _artifactDownloadHttpClient?.Dispose();
                _artifactDownloadHttpClient = null;
                _ownsArtifactDownloadHttpClient = false;
            }
        }
    }
}
