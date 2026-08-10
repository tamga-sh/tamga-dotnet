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
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Which of the 5 auth transports to use. <see langword="null"/> sends no credentials.</summary>
    public AuthTransport? Auth { get; init; }

    /// <summary>TOTP 2FA code, sent as <c>Tamga-OTP</c> on every authenticated request when set.</summary>
    public string? Otp { get; init; }
}

/// <summary>
/// One of the 5 auth transports the server accepts, in the order it tries them (docs/sdk.md §1).
/// Closed hierarchy of nested records — construct via the static factory methods.
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

    // GOTCHA (docs/sdk.md §1): every issued token gets the `tok-` prefix regardless of the
    // documented tok-/prod-/env-/activ-/lic- intent. This SDK deliberately never parses a token's
    // prefix for type detection anywhere — Bearer/BasicToken/QueryToken all treat their `Token`
    // value as an opaque string.
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
public sealed record JsonApiLinks
{
    /// <summary>The URL of the next page, or <see langword="null"/> if this is the last page.</summary>
    [JsonPropertyName("next")]
    public string? Next { get; init; }
}

/// <summary>
/// The JSON:API envelope for a keyset-paginated list endpoint (e.g. entitlements, components):
/// <c>{ data: [...], links: { next } }</c>. The <c>page[after]</c> cursor for the next page is
/// parsed out of <see cref="Links"/>.<c>Next</c> by the caller (see <c>TamgaClient</c>'s listing
/// methods), since it arrives as a full URL/query fragment rather than a bare cursor value.
/// </summary>
public sealed record JsonApiListDocument<TAttributes>
{
    /// <summary>The page of resources returned by the request.</summary>
    [JsonPropertyName("data")]
    public IReadOnlyList<JsonApiResource<TAttributes>> Data { get; init; } = Array.Empty<JsonApiResource<TAttributes>>();

    /// <summary>Non-standard meta information accompanying the response.</summary>
    [JsonPropertyName("meta")]
    public JsonElement? Meta { get; init; }

    /// <summary>Pagination/navigation links for the list response.</summary>
    [JsonPropertyName("links")]
    public JsonApiLinks? Links { get; init; }

    /// <summary>The errors returned instead of <see cref="Data"/> when the request failed.</summary>
    [JsonPropertyName("errors")]
    public IReadOnlyList<TamgaApiError>? Errors { get; init; }
}

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for every request/response body in this SDK: nulls
/// omitted on serialize, case-insensitive property matching on deserialize.
/// </summary>
public static class TamgaJsonOptions
{
    /// <summary>The shared <see cref="JsonSerializerOptions"/> instance used for (de)serializing Tamga API payloads.</summary>
    public static readonly JsonSerializerOptions Default = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
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

    private Uri BuildUri(string path, string? query)
    {
        var trimmedBase = _options.BaseUrl.TrimEnd('/');
        var accountSegment = Uri.EscapeDataString(_options.AccountId);
        var uri = $"{trimmedBase}/v1/accounts/{accountSegment}{path}";
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
    {
        var uri = BuildUri(path, query);
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
        // planned EE feature with no server-side read path (docs/sdk.md gap #7).

        return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
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

    private static TamgaApiException ParseAndMapError(string body, System.Net.HttpStatusCode status)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<TamgaApiErrorEnvelope>(body, TamgaJsonOptions.Default);
            var first = envelope?.Errors.FirstOrDefault();
            if (first is not null)
            {
                return TamgaErrorMapper.ToException(first);
            }
        }
        catch (JsonException)
        {
            // fall through to the generic error below
        }

        return new TamgaApiException(new TamgaApiError { Status = (ushort)status, Code = status.ToString(), Detail = body });
    }

    /// <summary>Reads the standard response headers this SDK surfaces (<c>Tamga-Version</c>, <c>Tamga-Edition</c>, <c>Tamga-Mode</c>, <c>X-Request-Id</c>).</summary>
    public static ResponseHeaders ReadResponseHeaders(HttpResponseMessage response) => new(
        response.Headers.TryGetValues("Tamga-Version", out var v) ? v.FirstOrDefault() : null,
        response.Headers.TryGetValues("Tamga-Edition", out var e) ? e.FirstOrDefault() : null,
        response.Headers.TryGetValues("Tamga-Mode", out var m) ? m.FirstOrDefault() : null,
        response.Headers.TryGetValues("X-Request-Id", out var r) ? r.FirstOrDefault() : null);

    // GOTCHA: no X-RateLimit-* response header parsing or 429 backoff here — declared in the
    // CORS allowlist only, never actually set/returned by any handler (docs/sdk.md gap #5).
}
