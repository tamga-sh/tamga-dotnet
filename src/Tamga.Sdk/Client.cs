using System.Text.Json;
using Tamga.Sdk.Models;

namespace Tamga.Sdk;

/// <summary>
/// The Tamga SDK's single public entry point — one <c>Task&lt;T&gt;</c>-returning method per
/// endpoint, every call taking a trailing <see cref="CancellationToken"/>. See
/// <c>docs/plans/tamga-dotnet.plan.md</c> §B–K for the endpoint-by-endpoint protocol reference
/// this class implements against.
/// </summary>
public sealed partial class TamgaClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TamgaTransport _transport;

    /// <summary>The options this client was constructed with.</summary>
    public TamgaClientOptions Options { get; }

    /// <summary>Constructs a client that owns and disposes its own internal <see cref="HttpClient"/>.</summary>
    public TamgaClient(TamgaClientOptions options)
        : this(options, new HttpClient { Timeout = options.Timeout }, ownsHttpClient: true)
    {
    }

    /// <summary>
    /// Constructs a client using an externally-injected <see cref="HttpClient"/> (e.g. from
    /// <c>IHttpClientFactory</c>) — the caller retains ownership and this instance never disposes it.
    /// </summary>
    public TamgaClient(TamgaClientOptions options, HttpClient httpClient)
        : this(options, httpClient, ownsHttpClient: false)
    {
    }

    private TamgaClient(TamgaClientOptions options, HttpClient httpClient, bool ownsHttpClient)
    {
        Options = options;
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _transport = new TamgaTransport(httpClient, options);
    }

    /// <summary>Disposes the internal <see cref="HttpClient"/> if this instance created and owns it; a no-op for externally-injected clients.</summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    // ---------------------------------------------------------------
    // §C License Validation
    //
    // Auth is currently NOT enforced server-side on any of these 3 endpoints, but this SDK still
    // always sends the configured Authorization header/query — forward-compatible once
    // enforcement lands (docs/sdk.md gap #3). Nothing special is needed here beyond what
    // TamgaTransport already applies from TamgaClientOptions.Auth.
    // ---------------------------------------------------------------

    /// <summary><c>POST /licenses/actions/validate-key</c> — validates by raw license key. No scope support.</summary>
    public async Task<ValidationResult> ValidateByKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        var doc = await _transport.SendJsonApiAsync<LicenseAttributes>(
            HttpMethod.Post,
            "/licenses/actions/validate-key",
            jsonBody: new ValidateByKeyRequest { Key = key },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return BuildValidationResult(doc);
    }

    /// <summary>
    /// <c>POST /licenses/{license_id}/actions/validate</c> — validates by license ID with an
    /// optional <see cref="Scope"/> constraint. <paramref name="skipTouch"/> suppresses the
    /// <c>last_validated_at</c> update side effect.
    /// </summary>
    public async Task<ValidationResult> ValidateByIdAsync(
        Guid licenseId,
        Scope? scope = null,
        bool skipTouch = false,
        CancellationToken cancellationToken = default)
    {
        object? body = scope is not null || skipTouch
            ? new ValidateByIdRequest { Meta = new ValidateByIdRequestMeta { Scope = scope, SkipTouch = skipTouch } }
            : null;

        var doc = await _transport.SendJsonApiAsync<LicenseAttributes>(
            HttpMethod.Post,
            $"/licenses/{licenseId}/actions/validate",
            jsonBody: body,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return BuildValidationResult(doc);
    }

    /// <summary>
    /// <c>GET /licenses/{license_id}/actions/validate</c> — quick-validate. Returns the flat,
    /// non-JSON:API-enveloped <c>{ ts, valid, detail, code }</c> body directly (no license resource).
    /// </summary>
    public async Task<QuickValidationResult> QuickValidateAsync(Guid licenseId, CancellationToken cancellationToken = default)
    {
        var (body, response) = await _transport.SendRawAsync(
            HttpMethod.Get,
            $"/licenses/{licenseId}/actions/validate",
            jsonApiContentType: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        response.Dispose();

        return JsonSerializer.Deserialize<QuickValidationResult>(body, TamgaJsonOptions.Default)
            ?? throw new TamgaApiException(new TamgaApiError { Status = 200, Code = "EMPTY_RESPONSE", Detail = "Quick-validate returned an empty body." });
    }

    // ---------------------------------------------------------------
    // §D License Check-In
    // ---------------------------------------------------------------

    /// <summary>
    /// <c>POST /licenses/{license_id}/actions/check-in</c> — no request body. On success returns
    /// the updated license resource with a bumped <c>last_check_in_at</c> (no <c>meta</c>).
    /// </summary>
    /// <exception cref="CheckInNotRequiredException">
    /// <c>422 CHECK_IN_NOT_REQUIRED</c> — a caller-logic error. Callers should check
    /// <c>policy.RequireCheckIn</c> before scheduling periodic check-ins rather than retry-looping
    /// on this exception.
    /// </exception>
    public async Task<License> CheckInAsync(Guid licenseId, CancellationToken cancellationToken = default)
    {
        var doc = await _transport.SendJsonApiAsync<LicenseAttributes>(
            HttpMethod.Post,
            $"/licenses/{licenseId}/actions/check-in",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return MapLicense(doc);
    }

    // ---------------------------------------------------------------
    // Internal mapping helpers
    // ---------------------------------------------------------------

    private static ValidationResult BuildValidationResult(JsonApiDocument<LicenseAttributes> doc)
    {
        var license = MapLicense(doc);
        var meta = doc.Meta?.Deserialize<ValidationMeta>(TamgaJsonOptions.Default)
            ?? throw new TamgaApiException(new TamgaApiError { Status = 200, Code = "MISSING_META", Detail = "Validation response had no meta object." });
        return new ValidationResult { License = license, Meta = meta };
    }

    private static License MapLicense(JsonApiDocument<LicenseAttributes> doc) =>
        License.FromResource(doc.Data ?? throw MissingDataError());
}
