using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamga.Sdk;

/// <summary>
/// Configuration for <see cref="TamgaClient"/>. <see cref="AccountId"/> and <see cref="BaseUrl"/>
/// are always required — the <c>{account_id}</c> URL segment is required in both singleplayer and
/// multiplayer server modes, there is no mode where it can be omitted.
/// </summary>
public sealed class TamgaClientOptions
{
    /// <summary>The Tamga account ID (or code) — always required, both singleplayer and multiplayer.</summary>
    public required string AccountId { get; init; }

    /// <summary>Scheme + host, e.g. <c>https://api.tamga.sh</c>. No trailing slash required.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>
    /// Sent as <c>Tamga-Version</c> on every request, sanitized (alphanumeric + <c>.</c>/<c>-</c>,
    /// max 32 chars). Pinned to this SDK's major version by default — NOT the server's own
    /// <c>"1.8"</c> default — so server-side API evolution doesn't silently change response shapes
    /// under a released SDK version.
    /// </summary>
    public string ApiVersion { get; init; } = "1";

    /// <summary>Request timeout applied to an internally-constructed <see cref="HttpClient"/>. Ignored when an external <see cref="HttpClient"/> is supplied to <see cref="TamgaClient"/>.</summary>
    /// <remarks>
    /// Deliberately longer than the server's own 30s request timeout. At exactly 30s the two race,
    /// and a slow request usually surfaced as a local <see cref="TaskCanceledException"/> instead
    /// of the server's <c>504</c> — which is the response that carries the <c>X-Request-Id</c> a
    /// support ticket needs. Sitting outside the server's deadline means the server's answer wins.
    /// </remarks>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How many times a rate-limited (<c>429</c>) request is retried before giving up.
    /// </summary>
    /// <remarks>
    /// Set to <c>0</c> to handle <c>429</c> yourself — the thrown exception still reports the
    /// status, and the server's <c>Retry-After</c> tells you how long to wait. Only requests safe
    /// to repeat are retried; creates never are.
    /// </remarks>
    public int MaxRetries { get; init; } = 3;

    /// <summary>Which of the 8 auth transports to use. <see langword="null"/> sends no credentials.</summary>
    public AuthTransport? Auth { get; init; }

    /// <summary>TOTP 2FA code, sent as <c>Tamga-OTP</c> on every authenticated request when set.</summary>
    public string? Otp { get; init; }
}

/// <summary>
/// One of the 8 auth transports the server accepts, listed in the order it tries them. Closed
/// hierarchy of nested records — construct the nested record you want directly.
/// </summary>
public abstract record AuthTransport
{
    private AuthTransport() { }

    /// <summary><c>Authorization: Bearer &lt;token&gt;</c> — default/first-tried transport.</summary>
    public sealed record Bearer(string Token) : AuthTransport;

    /// <summary><c>Authorization: Basic &lt;base64(email:password)&gt;</c>.</summary>
    public sealed record BasicEmailPassword(string Email, string Password) : AuthTransport;

    /// <summary><c>Authorization: Basic &lt;base64(token:)&gt;</c> — token as username, empty password.</summary>
    public sealed record BasicToken(string Token) : AuthTransport;

    /// <summary><c>Authorization: Basic &lt;base64(license:&lt;key&gt;)&gt;</c>.</summary>
    public sealed record BasicLicense(string LicenseKey) : AuthTransport;

    /// <summary><c>Authorization: License &lt;key&gt;</c> — primary transport for this embedded/client SDK.</summary>
    public sealed record License(string Key) : AuthTransport;

    /// <summary>
    /// <c>Cookie: Tamga-Session=&lt;uuid&gt;</c> + matching <c>Origin</c> header. Browser/portal-only; modeled for completeness, not the expected path for this SDK.
    /// </summary>
    /// <remarks>
    /// This header is set manually (bypassing <see cref="System.Net.CookieContainer"/>). If the
    /// supplied <see cref="HttpClient"/> was constructed with a handler that has
    /// <c>UseCookies = true</c> (the BCL default), that handler may also manage a <c>Cookie</c>
    /// header itself, which can conflict with or duplicate this one. Callers using this transport
    /// should construct their <see cref="HttpClient"/>'s handler with <c>UseCookies = false</c>.
    /// </remarks>
    public sealed record Cookie(string SessionId, string Origin) : AuthTransport;

    /// <summary><c>?token=&lt;token&gt;</c> query parameter fallback.</summary>
    public sealed record QueryToken(string Token) : AuthTransport;

    /// <summary><c>?auth=&lt;token&gt;</c> query parameter fallback.</summary>
    public sealed record QueryAuth(string Token) : AuthTransport;

    // GOTCHA (Tamga API protocol specification §1): every issued token gets the `tok-` prefix
    // regardless of the documented tok-/prod-/env-/activ-/lic- intent. This SDK deliberately
    // never parses a token's prefix for type detection anywhere — Bearer/BasicToken/QueryToken
    // all treat their `Token` value as an opaque string.
}

/// <summary>Alphanumeric + <c>.</c>/<c>-</c> sanitization for the <c>Tamga-Version</c> header, max 32 chars.</summary>
public static class TamgaVersionSanitizer
{
    /// <summary>The maximum length, in characters, of the sanitized version string.</summary>
    public const int MaxLength = 32;

    /// <summary>
    /// Strips <paramref name="version"/> down to ASCII alphanumerics and <c>.</c>/<c>-</c>, truncating
    /// to <see cref="MaxLength"/> characters, for safe use as the <c>Tamga-Version</c> header value.
    /// </summary>
    /// <param name="version">The raw version string to sanitize.</param>
    /// <returns>The sanitized, truncated version string.</returns>
    public static string Sanitize(string version)
    {
        var builder = new StringBuilder(Math.Min(version.Length, MaxLength));
        foreach (var c in version)
        {
            if (builder.Length >= MaxLength)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '-')
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}

/// <summary>A JSON:API resource identifier: <c>{ "type": "...", "id": "..." }</c>.</summary>
public sealed record JsonApiResourceIdentifier
{
    /// <summary>The resource type discriminator.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    /// <summary>The resource's unique identifier.</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; init; }
}

/// <summary>A single JSON:API relationship: <c>{ "data": { "type": "...", "id": "..." } }</c>.</summary>
public sealed record JsonApiRelationship
{
    /// <summary>The related resource's linkage, or <see langword="null"/> if the relationship is empty.</summary>
    [JsonPropertyName("data")]
    public JsonApiResourceIdentifier? Data { get; init; }
}

/// <summary>A JSON:API resource object: <c>{ type, id, attributes, relationships }</c>.</summary>
public sealed record JsonApiResource<TAttributes>
{
    /// <summary>The resource type discriminator.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    /// <summary>The resource's unique identifier.</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    /// <summary>The resource's attributes payload.</summary>
    [JsonPropertyName("attributes")]
    public TAttributes? Attributes { get; init; }

    /// <summary>The resource's related-resource linkages, keyed by relationship name.</summary>
    [JsonPropertyName("relationships")]
    public Dictionary<string, JsonApiRelationship>? Relationships { get; init; }
}

/// <summary>
/// The generic JSON:API envelope this SDK receives from every endpoint except quick-validate
/// (see <see cref="TamgaClient.QuickValidateAsync"/>, which has its own flat, non-enveloped response
/// type): <c>{ data, meta, errors }</c>.
/// </summary>
public sealed record JsonApiDocument<TAttributes>
{
    /// <summary>The primary resource returned by the request.</summary>
    [JsonPropertyName("data")]
    public JsonApiResource<TAttributes>? Data { get; init; }

    /// <summary>Non-standard meta information accompanying the response.</summary>
    [JsonPropertyName("meta")]
    public JsonElement? Meta { get; init; }

    /// <summary>The errors returned instead of <see cref="Data"/> when the request failed.</summary>
    [JsonPropertyName("errors")]
    public IReadOnlyList<TamgaApiError>? Errors { get; init; }

    /// <summary>Looks up a related resource's ID by relationship name (e.g. <c>"policy"</c>), or <see langword="null"/> if absent.</summary>
    public Guid? RelationshipId(string name) =>
        Data?.Relationships is { } rels && rels.TryGetValue(name, out var rel) ? rel.Data?.Id : null;
}

/// <summary>The <c>data</c> object of a JSON:API create request: <c>{ type, attributes, relationships }</c>.</summary>
public sealed record JsonApiCreateRequestData<TAttributes>
{
    /// <summary>The resource type discriminator.</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>The attributes to create the resource with.</summary>
    [JsonPropertyName("attributes")]
    public required TAttributes Attributes { get; init; }

    /// <summary>The related-resource linkages to create the resource with, keyed by relationship name.</summary>
    [JsonPropertyName("relationships")]
    public Dictionary<string, JsonApiRelationship>? Relationships { get; init; }
}

/// <summary>A JSON:API create request body: <c>{ "data": {...} }</c>.</summary>
public sealed record JsonApiCreateRequest<TAttributes>
{
    /// <summary>The resource data to create.</summary>
    [JsonPropertyName("data")]
    public required JsonApiCreateRequestData<TAttributes> Data { get; init; }
}

/// <summary>The <c>links</c> object on a keyset-paginated JSON:API list response.</summary>
/// <remarks>
/// GOTCHA: the server never emits this. Every serializer builds its document with
/// <c>links: None</c> and the field is <c>skip_serializing_if = "Option::is_none"</c>, so no
/// response the API can produce carries a <c>links</c> key at all. Kept for wire-shape
/// completeness and source compatibility only — <see cref="Next"/> is always
/// <see langword="null"/>, and nothing in this SDK derives a pagination cursor from it. See
/// <c>TamgaClient</c>'s listing methods for how the cursor is actually synthesized.
/// </remarks>
public sealed record JsonApiLinks
{
    /// <summary>Always <see langword="null"/> — the server emits no <c>links</c> object. See the type-level remarks.</summary>
    [JsonPropertyName("next")]
    public string? Next { get; init; }
}

/// <summary>
/// The JSON:API envelope for a keyset-paginated list endpoint (e.g. entitlements, components):
/// <c>{ data: [...] }</c>. <see cref="Links"/> is modeled but never populated by the server (see
/// <see cref="JsonApiLinks"/>), so the <c>page[after]</c> cursor is synthesized from the last item
/// of a full page instead — see <c>TamgaClient</c>'s listing methods.
/// </summary>
public sealed record JsonApiListDocument<TAttributes>
{
    /// <summary>The page of resources returned by the request.</summary>
    [JsonPropertyName("data")]
    public IReadOnlyList<JsonApiResource<TAttributes>> Data { get; init; } = Array.Empty<JsonApiResource<TAttributes>>();

    /// <summary>Non-standard meta information accompanying the response.</summary>
    [JsonPropertyName("meta")]
    public JsonElement? Meta { get; init; }

    /// <summary>Always <see langword="null"/> — the server emits no <c>links</c> object. See <see cref="JsonApiLinks"/>.</summary>
    [JsonPropertyName("links")]
    public JsonApiLinks? Links { get; init; }

    /// <summary>The errors returned instead of <see cref="Data"/> when the request failed.</summary>
    [JsonPropertyName("errors")]
    public IReadOnlyList<TamgaApiError>? Errors { get; init; }
}

/// <summary>
/// The <c>meta.page</c> object of an OFFSET-paginated JSON:API list response — the machine
/// collection is the only listing in this SDK that carries one.
/// </summary>
/// <remarks>
/// Not a keyset cursor. The server builds this from a real <c>COUNT(*)</c> over the same filter
/// the rows were selected with, so <see cref="Total"/> and <see cref="TotalPages"/> are exact and
/// end-of-list needs no row-count guesswork — unlike the component listing, where the absence of
/// any pagination metadata forces the cursor to be synthesized from a full page (see
/// <see cref="JsonApiLinks"/>).
///
/// Note the casing: <c>totalPages</c> is camelCase on the wire (an explicit
/// <c>#[serde(rename)]</c> server-side) while <c>number</c>/<c>size</c>/<c>total</c> are plain.
/// It is NOT <c>total_pages</c>.
/// </remarks>
public sealed record JsonApiPageMeta
{
    /// <summary>The 1-based number of the page that was returned.</summary>
    [JsonPropertyName("number")]
    public int Number { get; init; }

    /// <summary>The page size the server actually applied, after its own <c>1..100</c> clamp.</summary>
    [JsonPropertyName("size")]
    public int Size { get; init; }

    /// <summary>Total rows matching the filters — not the table size.</summary>
    [JsonPropertyName("total")]
    public long Total { get; init; }

    /// <summary>Total pages at this <see cref="Size"/>; <c>0</c> when <see cref="Total"/> is <c>0</c>.</summary>
    [JsonPropertyName("totalPages")]
    public int TotalPages { get; init; }
}

/// <summary>The <c>meta</c> object of an offset-paginated list response: <c>{ "page": { … } }</c>.</summary>
public sealed record JsonApiListMeta
{
    /// <summary>The pagination block, or <see langword="null"/> on a listing that does not paginate by offset.</summary>
    [JsonPropertyName("page")]
    public JsonApiPageMeta? Page { get; init; }
}

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for every request/response body in this SDK: nulls
/// omitted on serialize, case-insensitive property matching on deserialize, and numbers accepted
/// from JSON strings on deserialize.
/// </summary>
/// <remarks>
/// CRITICAL — <see cref="JsonNumberHandling.AllowReadingFromString"/> is load-bearing, not a
/// nicety. The server serializes a JSON:API error's <c>status</c> as a <em>string</em>
/// (<c>status.as_u16().to_string()</c>), so the wire shape is <c>"status": "422"</c>, not
/// <c>"status": 422</c>. Without this flag, <see cref="TamgaApiError.Status"/>
/// (a <see cref="ushort"/>) fails to bind, the whole <see cref="TamgaApiErrorEnvelope"/>
/// deserialization throws, and <see cref="TamgaErrorMapper.ToException(TamgaApiError, Exception?)"/>
/// is never reached — every
/// typed exception in this SDK becomes unreachable and every API error degrades to a bare
/// <see cref="TamgaApiException"/> whose <c>code</c> is the HTTP status name. Do not remove it.
/// </remarks>
public static class TamgaJsonOptions
{
    /// <summary>The shared <see cref="JsonSerializerOptions"/> instance used for (de)serializing Tamga API payloads.</summary>
    public static readonly JsonSerializerOptions Default = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}

/// <summary>
/// Thin HTTP transport: builds the account-scoped base URL, applies the configured
/// <see cref="AuthTransport"/> and standard headers, and executes requests via
/// <see cref="HttpClient.SendAsync(HttpRequestMessage, CancellationToken)"/>.
/// </summary>
public sealed class TamgaTransport
{
    private readonly HttpClient _httpClient;
    private readonly TamgaClientOptions _options;

    /// <summary>
    /// Creates a transport that sends requests through <paramref name="httpClient"/>, scoped to the
    /// account and configured with the auth transport in <paramref name="options"/>.
    /// </summary>
    /// <param name="httpClient">The <see cref="HttpClient"/> used to send every request.</param>
    /// <param name="options">The account, base URL, auth, and other request configuration.</param>
    public TamgaTransport(HttpClient httpClient, TamgaClientOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    /// <summary>Response headers surfaced from the most recently completed request, if any were present.</summary>
    public sealed record ResponseHeaders(string? TamgaVersion, string? TamgaEdition, string? TamgaMode, string? RequestId);

    /// <summary>
    /// The <c>x-ratelimit-*</c> response headers, read off a response that carried them. A
    /// deliberately separate type from <see cref="ResponseHeaders"/>, which is a positional record
    /// whose shape cannot be extended without changing its constructor and <c>Deconstruct</c>
    /// signatures.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rate-limit middleware sets all four of these on the response it returns
    /// (<c>shared/rate_limit/middleware.rs:140-143</c>), on the throttled <c>429</c> and on the
    /// request it lets through alike, and all four are in the CORS expose list
    /// (<c>router.rs:123-126</c>) so a browser client can read them too. Until 2026-08-21 this SDK
    /// documented them as declared-but-never-set; that was wrong.
    /// </para>
    /// <para>
    /// Every member is nullable and every one can legitimately be <see langword="null"/>: the
    /// middleware returns early without setting anything when the server has no rate limiter
    /// configured (<c>state.rate_limiter</c> is an <c>Option</c> that is <c>None</c> whenever the
    /// Redis pool could not be built), and it also skips <c>OPTIONS</c> preflight. Absent is
    /// therefore NOT the same as exhausted — check <see cref="IsPresent"/> before reading
    /// <see cref="Remaining"/> as a budget, or a client on an unlimited server reads "0 left".
    /// </para>
    /// </remarks>
    /// <param name="Limit">Bucket capacity for the matched route — <c>x-ratelimit-limit</c>. Auth-accepting routes get a tighter budget than everything else.</param>
    /// <param name="Remaining">Requests left in the current window — <c>x-ratelimit-remaining</c>. Floored at 0 server-side, so it never goes negative.</param>
    /// <param name="Reset">When the window resets, as an ABSOLUTE Unix time in seconds — <c>x-ratelimit-reset</c>. Not a delay: the server computes it as <c>now + ttl</c>. Use <see cref="ResetAt"/> rather than treating this as a duration, and use <c>Retry-After</c> (already honoured by the transport's own backoff) when what you want is how long to wait.</param>
    /// <param name="Window">Window length in seconds — <c>x-ratelimit-window</c>. Currently always <c>1</c>: the per-second figure is the refill rate and the burst allowance is the capacity.</param>
    public sealed record RateLimitInfo(long? Limit, long? Remaining, long? Reset, long? Window)
    {
        /// <summary>
        /// <see langword="true"/> when the response carried at least one <c>x-ratelimit-*</c>
        /// header — i.e. a rate limiter is actually running in front of this server.
        /// </summary>
        public bool IsPresent => Limit is not null || Remaining is not null || Reset is not null || Window is not null;

        /// <summary><see cref="Reset"/> as a point in time, or <see langword="null"/> when the header was absent or unparseable.</summary>
        public DateTimeOffset? ResetAt => Reset is { } reset ? DateTimeOffset.FromUnixTimeSeconds(reset) : null;
    }

    private Uri BuildUri(string path, string? query)
    {
        var accountSegment = Uri.EscapeDataString(_options.AccountId);
        return BuildUnscopedUri($"/v1/accounts/{accountSegment}{path}", query);
    }

    /// <summary>
    /// Builds a URL from the configured origin WITHOUT the <c>/v1/accounts/{account_id}</c> prefix
    /// <see cref="BuildUri"/> adds unconditionally.
    /// </summary>
    /// <remarks>
    /// Exists for exactly one route today, <c>GET /v1/health</c>, which is registered at the
    /// server's root and is not account-scoped. Until this existed the SDK could not address it at
    /// all — not because the server refused, but because every URL this client built went through
    /// the account prefix. Pass a path that already starts at <c>/v1</c>.
    /// </remarks>
    private Uri BuildUnscopedUri(string path, string? query)
    {
        var uri = _options.BaseUrl.TrimEnd('/') + path;
        if (!string.IsNullOrEmpty(query))
        {
            uri += (uri.Contains('?') ? "&" : "?") + query;
        }

        return new Uri(uri);
    }

    private void ApplyAuth(HttpRequestMessage request, ref Uri uri)
    {
        switch (_options.Auth)
        {
            case AuthTransport.Bearer bearer:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer.Token);
                break;
            case AuthTransport.BasicEmailPassword basic:
                request.Headers.Authorization = BasicAuth($"{basic.Email}:{basic.Password}");
                break;
            case AuthTransport.BasicToken basicToken:
                request.Headers.Authorization = BasicAuth($"{basicToken.Token}:");
                break;
            case AuthTransport.BasicLicense basicLicense:
                request.Headers.Authorization = BasicAuth($"license:{basicLicense.LicenseKey}");
                break;
            case AuthTransport.License license:
                request.Headers.Authorization = new AuthenticationHeaderValue("License", license.Key);
                break;
            case AuthTransport.Cookie cookie:
                request.Headers.Add("Cookie", $"Tamga-Session={cookie.SessionId}");
                request.Headers.Add("Origin", cookie.Origin);
                break;
            case AuthTransport.QueryToken queryToken:
                uri = AppendQuery(uri, "token", queryToken.Token);
                break;
            case AuthTransport.QueryAuth queryAuth:
                uri = AppendQuery(uri, "auth", queryAuth.Token);
                break;
        }
    }

    private static AuthenticationHeaderValue BasicAuth(string userInfo) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(userInfo)));

    private static Uri AppendQuery(Uri uri, string key, string value)
    {
        var separator = uri.Query.Length > 0 ? "&" : "?";
        return new Uri($"{uri}{separator}{key}={Uri.EscapeDataString(value)}");
    }

    /// <summary>
    /// Sends a request and returns the raw <see cref="HttpResponseMessage"/> with all standard
    /// headers/auth/content-type applied. Callers are responsible for status checking and body
    /// parsing (see <see cref="SendJsonApiAsync{TAttributes}"/> for the common case).
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string? query = null,
        object? jsonBody = null,
        bool jsonApiContentType = true,
        CancellationToken cancellationToken = default)
        => await SendToUriAsync(BuildUri(path, query), method, path, jsonBody, jsonApiContentType, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Same as <see cref="SendAsync"/> but addresses a path at the configured origin's root,
    /// skipping the <c>/v1/accounts/{account_id}</c> prefix — see <see cref="BuildUnscopedUri"/>.
    /// </summary>
    /// <param name="method">The HTTP method to send.</param>
    /// <param name="path">A root-relative path that already starts at <c>/v1</c>, e.g. <c>/v1/health</c>.</param>
    /// <param name="query">The raw query string to append, already escaped, or <see langword="null"/>.</param>
    /// <param name="jsonApiContentType">Whether to negotiate <c>application/vnd.api+json</c>; <see langword="false"/> for the plain-JSON routes.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<HttpResponseMessage> SendUnscopedAsync(
        HttpMethod method,
        string path,
        string? query = null,
        bool jsonApiContentType = true,
        CancellationToken cancellationToken = default)
        => await SendToUriAsync(BuildUnscopedUri(path, query), method, path, jsonBody: null, jsonApiContentType, cancellationToken).ConfigureAwait(false);

    private async Task<HttpResponseMessage> SendToUriAsync(
        Uri uri,
        HttpMethod method,
        string path,
        object? jsonBody,
        bool jsonApiContentType,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, uri);
        ApplyAuth(request, ref uri);
        request.RequestUri = uri;

        var mediaType = jsonApiContentType ? "application/vnd.api+json" : "application/json";
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(mediaType));

        if (jsonBody is not null)
        {
            var json = JsonSerializer.Serialize(jsonBody, jsonBody.GetType(), TamgaJsonOptions.Default);
            request.Content = new StringContent(json, Encoding.UTF8, mediaType);
        }

        request.Headers.Add("Tamga-Version", TamgaVersionSanitizer.Sanitize(_options.ApiVersion));
        if (!string.IsNullOrEmpty(_options.Otp))
        {
            request.Headers.Add("Tamga-OTP", _options.Otp);
        }

        // GOTCHA: do NOT add a Tamga-Environment request header here — it's an unimplemented,
        // planned EE feature with no server-side read path (Tamga API protocol specification
        // gap #7).

        return await SendWithRetryAsync(request, method, path, jsonBody, mediaType, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// How many times a rate-limited (<c>429</c>) request is retried before giving up.
    /// </summary>
    /// <remarks>
    /// Three rides out a short burst without turning a sustained 429 into a request that hangs
    /// for minutes.
    /// </remarks>
    public const int DefaultMaxRetries = 3;

    /// <summary>How much of a <c>Retry-After</c> is honoured, in seconds.</summary>
    private const int MaxRetryAfterSeconds = 60;

    /// <summary>
    /// The seven <c>POST</c> action suffixes that are safe to repeat after a <c>429</c> — see
    /// <see cref="IsRetryable"/> for why these and nothing else.
    /// </summary>
    /// <remarks>
    /// <c>/actions/ping-heartbeat</c> and <c>/actions/reset-heartbeat</c> are listed explicitly:
    /// neither ends with <c>/actions/ping</c> (that suffix is the <em>process</em> ping route), so
    /// both were silently excluded and a throttled heartbeat was dropped — which flips a machine to
    /// <c>DEAD</c>, and on a policy that actually sets <c>require_heartbeat</c> (it defaults to
    /// <c>FALSE</c>) eventually gets it culled. Both are bare idempotent state writes server-side
    /// (<c>UPDATE … SET last_heartbeat_at = NOW()</c>), so repeating them cannot burn a seat.
    /// </remarks>
    private static readonly string[] RetryablePostSuffixes =
    [
        "/actions/validate",
        "/actions/validate-key",
        "/actions/check-in",
        "/actions/check-out",
        "/actions/ping",
        "/actions/ping-heartbeat",
        "/actions/reset-heartbeat",
    ];

    /// <summary>Is this request safe to repeat after a <c>429</c>?</summary>
    /// <remarks>
    /// <c>GET</c> always is. Among the <c>POST</c>s only the licensing <em>actions</em> are — they
    /// are effectively idempotent (validate, check in/out, ping a heartbeat) and they are
    /// precisely the calls a client makes on a timer, so they are the ones that hit the rate limit
    /// in the first place.
    ///
    /// Creates are deliberately excluded: retrying <c>POST /machines</c> risks a second activation
    /// burning a second seat, and only the caller knows whether that is acceptable.
    /// </remarks>
    public static bool IsRetryable(HttpMethod method, string path)
    {
        if (method == HttpMethod.Get)
        {
            return true;
        }

        if (method != HttpMethod.Post)
        {
            return false;
        }

        foreach (var suffix in RetryablePostSuffixes)
        {
            if (path.EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>How long to wait before retry number <paramref name="attempt"/> (0-based).</summary>
    /// <remarks>
    /// Prefers the server's <c>Retry-After</c> — it knows when the bucket refills, and guessing
    /// wastes the budget — but caps it, so a misconfigured or hostile proxy cannot park the caller
    /// for an hour on one header. Otherwise exponential backoff with jitter, because a fleet that
    /// all retries on the same schedule reconverges into the spike it was backing off from.
    /// </remarks>
    public static TimeSpan RetryDelay(int attempt, int? retryAfterSeconds)
    {
        if (retryAfterSeconds is { } secs)
        {
            return TimeSpan.FromSeconds(Math.Min(secs, MaxRetryAfterSeconds));
        }

        var baseSeconds = 1 << Math.Min(attempt, 5);
        return TimeSpan.FromSeconds(baseSeconds) + TimeSpan.FromMilliseconds(Random.Shared.Next(1000));
    }

    /// <summary>Reads <c>Retry-After</c> as delta-seconds.</summary>
    /// <remarks>
    /// The HTTP-date form is ignored deliberately: the server sends seconds, and misreading a date
    /// as a duration would be far worse than falling back to the client's own backoff.
    /// </remarks>
    public static int? ParseRetryAfter(HttpResponseMessage response)
        => response.Headers.RetryAfter?.Delta is { } delta ? (int)delta.TotalSeconds : null;

    /// <summary>
    /// Sends the request, transparently retrying while the server answers <c>429</c>.
    /// </summary>
    /// <remarks>
    /// Credential-accepting endpoints run on a tight per-IP budget (5 req/s by default) and the
    /// calls a licensing client makes on a timer are exactly the ones inside it. Without backoff,
    /// one throttled request becomes a sustained burst that keeps the bucket empty and the client
    /// never recovers on its own.
    ///
    /// An <see cref="HttpRequestMessage"/> cannot be sent twice, so each attempt gets a fresh copy.
    /// </remarks>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpRequestMessage prototype,
        HttpMethod method,
        string path,
        object? jsonBody,
        string mediaType,
        CancellationToken cancellationToken)
    {
        var retryable = IsRetryable(method, path);

        for (var attempt = 0; ; attempt++)
        {
            var request = attempt == 0 ? prototype : CloneRequest(prototype, jsonBody, mediaType);
            var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.TooManyRequests
                || !retryable
                || attempt >= _options.MaxRetries)
            {
                return response;
            }

            var delay = RetryDelay(attempt, ParseRetryAfter(response));
            response.Dispose();
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static HttpRequestMessage CloneRequest(
        HttpRequestMessage prototype,
        object? jsonBody,
        string mediaType)
    {
        var clone = new HttpRequestMessage(prototype.Method, prototype.RequestUri);
        foreach (var header in prototype.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (jsonBody is not null)
        {
            var json = JsonSerializer.Serialize(jsonBody, jsonBody.GetType(), TamgaJsonOptions.Default);
            clone.Content = new StringContent(json, Encoding.UTF8, mediaType);
        }

        return clone;
    }

    /// <summary>
    /// Sends a JSON:API request, throws the mapped typed exception (see
    /// <see cref="TamgaErrorMapper"/>) on a non-success status, and deserializes the
    /// <see cref="JsonApiDocument{TAttributes}"/> envelope on success.
    /// </summary>
    public async Task<JsonApiDocument<TAttributes>> SendJsonApiAsync<TAttributes>(
        HttpMethod method,
        string path,
        string? query = null,
        object? jsonBody = null,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(method, path, query, jsonBody, jsonApiContentType: true, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw ParseAndMapError(body, response.StatusCode);
        }

        return JsonSerializer.Deserialize<JsonApiDocument<TAttributes>>(body, TamgaJsonOptions.Default)
            ?? throw new TamgaApiException(new TamgaApiError { Status = (ushort)response.StatusCode, Code = "EMPTY_RESPONSE", Detail = "Server returned an empty body." });
    }

    /// <summary>Same as <see cref="SendJsonApiAsync{TAttributes}"/> but for a keyset-paginated list response (<see cref="JsonApiListDocument{TAttributes}"/>).</summary>
    public async Task<JsonApiListDocument<TAttributes>> SendJsonApiListAsync<TAttributes>(
        HttpMethod method,
        string path,
        string? query = null,
        object? jsonBody = null,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(method, path, query, jsonBody, jsonApiContentType: true, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw ParseAndMapError(body, response.StatusCode);
        }

        return JsonSerializer.Deserialize<JsonApiListDocument<TAttributes>>(body, TamgaJsonOptions.Default)
            ?? throw new TamgaApiException(new TamgaApiError { Status = (ushort)response.StatusCode, Code = "EMPTY_RESPONSE", Detail = "Server returned an empty body." });
    }

    /// <summary>
    /// Sends a request with no envelope expectation and returns the raw response body string on
    /// success (mapping errors the same way as <see cref="SendJsonApiAsync{TAttributes}"/>) — used
    /// by the quick-validate endpoint (plain JSON, no <c>data</c> key) and raw-bytes checkout.
    /// </summary>
    public async Task<(string Body, HttpResponseMessage Response)> SendRawAsync(
        HttpMethod method,
        string path,
        string? query = null,
        object? jsonBody = null,
        bool jsonApiContentType = false,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(method, path, query, jsonBody, jsonApiContentType, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw ParseAndMapError(body, response.StatusCode);
        }

        return (body, response);
    }

    /// <summary>
    /// Same as <see cref="SendJsonApiAsync{TAttributes}"/> but treats <c>204 No Content</c> (and an
    /// empty success body) as a legitimate answer, returning <see langword="null"/> rather than
    /// throwing.
    /// </summary>
    /// <remarks>
    /// One route needs this: <c>GET /releases/actions/upgrade</c> answers <c>204</c> whenever it
    /// has no release to offer. Do not reuse it to paper over an unexpectedly empty body on a route
    /// that always returns a resource — <see cref="SendJsonApiAsync{TAttributes}"/>'s
    /// <c>EMPTY_RESPONSE</c> error is the correct outcome there.
    /// </remarks>
    public async Task<JsonApiDocument<TAttributes>?> SendJsonApiAllowNoContentAsync<TAttributes>(
        HttpMethod method,
        string path,
        string? query = null,
        object? jsonBody = null,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(method, path, query, jsonBody, jsonApiContentType: true, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw ParseAndMapError(body, response.StatusCode);
        }

        return response.StatusCode == HttpStatusCode.NoContent || string.IsNullOrWhiteSpace(body)
            ? null
            : JsonSerializer.Deserialize<JsonApiDocument<TAttributes>>(body, TamgaJsonOptions.Default);
    }

    /// <summary>
    /// Sends a request to a root-relative (non-account-scoped) path and returns the raw success
    /// body, mapping errors the same way as <see cref="SendJsonApiAsync{TAttributes}"/>.
    /// </summary>
    /// <param name="method">The HTTP method to send.</param>
    /// <param name="path">A root-relative path that already starts at <c>/v1</c>, e.g. <c>/v1/health</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<string> SendUnscopedRawAsync(
        HttpMethod method,
        string path,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendUnscopedAsync(method, path, query: null, jsonApiContentType: false, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw ParseAndMapError(body, response.StatusCode);
        }

        return body;
    }

    /// <summary>Sends a request expecting no meaningful response body (e.g. <c>DELETE</c>), mapping errors the same way as <see cref="SendJsonApiAsync{TAttributes}"/>.</summary>
    public async Task DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Delete, path, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw ParseAndMapError(body, response.StatusCode);
        }
    }

    /// <summary>
    /// Parses a JSON:API error envelope and maps its first error to a typed exception, degrading
    /// as little as possible when the body is not the envelope this SDK expects.
    /// </summary>
    /// <remarks>
    /// The fallback path used to overwrite the server's <c>code</c> with the HTTP status name
    /// (<c>"UnprocessableEntity"</c>), destroying the only stable value a caller can dispatch on —
    /// and it did so silently, so a malformed envelope was indistinguishable from a well-formed
    /// one this SDK simply could not bind. Now the server's own <c>code</c>/<c>detail</c> are
    /// recovered from the raw body whenever they are present at all, the synthesized code is
    /// clearly marked (<c>UNPARSEABLE_ERROR_BODY</c>) when they are not, and the underlying
    /// <see cref="JsonException"/> is preserved instead of being swallowed — on BOTH
    /// <see cref="TamgaApiException.ErrorBodyParseFailure"/> (for SDK-aware callers) and the
    /// exception's <see cref="Exception.InnerException"/> (for <see cref="Exception.ToString"/>,
    /// logging sinks and APM agents, which walk that chain automatically and would otherwise never
    /// see the diagnostic).
    /// </remarks>
    private static TamgaApiException ParseAndMapError(string body, System.Net.HttpStatusCode status)
    {
        JsonException? parseFailure = null;
        try
        {
            var envelope = JsonSerializer.Deserialize<TamgaApiErrorEnvelope>(body, TamgaJsonOptions.Default);
            var first = envelope?.Errors.FirstOrDefault();
            if (first is not null)
            {
                return TamgaErrorMapper.ToException(first);
            }
        }
        catch (JsonException ex)
        {
            parseFailure = ex;
        }

        var recovered = RecoverErrorFieldsFromRawBody(body);
        var error = new TamgaApiError
        {
            Status = (ushort)status,
            Code = recovered.Code ?? "UNPARSEABLE_ERROR_BODY",
            Detail = recovered.Detail ?? body,
        };

        // Still map through the typed mapper: a recovered `code` is exactly as dispatchable as one
        // that bound cleanly, and degrading it to the base type would cost the caller the very
        // thing the recovery was for. The parse failure has to go in through the constructor —
        // InnerException cannot be assigned after the fact, and assigning only the typed property
        // (as this used to) leaves ex.ToString() and every generic log sink blind to it.
        return TamgaErrorMapper.ToException(error, parseFailure);
    }

    /// <summary>
    /// Last-ditch recovery of <c>code</c>/<c>detail</c> from an error body that would not bind to
    /// <see cref="TamgaApiErrorEnvelope"/> — walks the raw JSON document instead of the typed
    /// envelope, so a single unexpected field elsewhere in the payload cannot cost the caller the
    /// server's stable error code.
    /// </summary>
    private static (string? Code, string? Detail) RecoverErrorFieldsFromRawBody(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("errors", out var errors)
                || errors.ValueKind != JsonValueKind.Array
                || errors.GetArrayLength() == 0)
            {
                return (null, null);
            }

            var first = errors[0];
            if (first.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var code = first.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String
                ? codeElement.GetString()
                : null;
            var detail = first.TryGetProperty("detail", out var detailElement) && detailElement.ValueKind == JsonValueKind.String
                ? detailElement.GetString()
                : null;
            return (code, detail);
        }
        catch (JsonException)
        {
            // The body is not JSON at all (e.g. the bare `axum::extract::Query` rejection on the
            // upgrade-check route answers plain text). Nothing to recover.
            return (null, null);
        }
    }

    /// <summary>Reads the standard response headers this SDK surfaces (<c>Tamga-Version</c>, <c>Tamga-Edition</c>, <c>Tamga-Mode</c>, <c>X-Request-Id</c>).</summary>
    public static ResponseHeaders ReadResponseHeaders(HttpResponseMessage response) => new(
        response.Headers.TryGetValues("Tamga-Version", out var v) ? v.FirstOrDefault() : null,
        response.Headers.TryGetValues("Tamga-Edition", out var e) ? e.FirstOrDefault() : null,
        response.Headers.TryGetValues("Tamga-Mode", out var m) ? m.FirstOrDefault() : null,
        response.Headers.TryGetValues("X-Request-Id", out var r) ? r.FirstOrDefault() : null);

    /// <summary>
    /// Reads the <c>x-ratelimit-*</c> response headers (<c>limit</c>, <c>remaining</c>,
    /// <c>reset</c>, <c>window</c>) off a response.
    /// </summary>
    /// <remarks>
    /// Deliberately a second accessor rather than four more members on
    /// <see cref="ReadResponseHeaders"/>'s <see cref="ResponseHeaders"/>: that type is a positional
    /// record, so widening it would change its primary constructor and its <c>Deconstruct</c>
    /// signature — a break for every caller that constructs or deconstructs one, in service of
    /// values that come from a different middleware and are absent under different conditions.
    /// <para>
    /// A missing or non-numeric header becomes <see langword="null"/>, never an exception: this is
    /// diagnostic metadata and follows the same rule as <see cref="ReadResponseHeaders"/>. Parsing
    /// is invariant-culture and refuses a sign or any other decoration, so a malformed value reads
    /// as absent rather than as a plausible-looking wrong number.
    /// </para>
    /// <para>
    /// Reading these is separate from surviving a <c>429</c>, which the transport already handles
    /// on its own: see <c>SendWithRetryAsync</c>/<c>IsRetryable</c>/<c>RetryDelay</c>/
    /// <c>ParseRetryAfter</c> above. Use this to pace ahead of the limit, not to recover from it.
    /// </para>
    /// </remarks>
    public static RateLimitInfo ReadRateLimitInfo(HttpResponseMessage response) => new(
        ReadHeaderAsInt64(response, "x-ratelimit-limit"),
        ReadHeaderAsInt64(response, "x-ratelimit-remaining"),
        ReadHeaderAsInt64(response, "x-ratelimit-reset"),
        ReadHeaderAsInt64(response, "x-ratelimit-window"));

    private static long? ReadHeaderAsInt64(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values))
        {
            return null;
        }

        var raw = values.FirstOrDefault();
        return long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}
