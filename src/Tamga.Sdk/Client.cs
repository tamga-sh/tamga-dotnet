using System.Text.Json;
using Tamga.Sdk.Models;

namespace Tamga.Sdk;

/// <summary>
/// The Tamga SDK's single public entry point — one <c>Task&lt;T&gt;</c>-returning method per
/// endpoint, every call taking a trailing <see cref="CancellationToken"/>.
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
    // Auth IS enforced. License-key auth in particular is off by default: the server accepts an
    // `Authorization: License <key>` credential only when the license's policy has
    // `authentication_strategy` set to LICENSE or MIXED, and that column defaults to 'TOKEN'.
    // Against a default policy every call on a license key answers 401 LICENSE_NOT_ALLOWED
    // (LicenseNotAllowedException) — a configuration precondition, not a transient failure, so
    // retrying will not help. Suspended licenses (401 LICENSE_SUSPENDED) and expired licenses
    // under a REVOKE_ACCESS policy (401 LICENSE_EXPIRED) are refused at the same front door,
    // before any per-endpoint check runs.
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
    /// <remarks>
    /// Unlike <see cref="ValidateByIdAsync"/>, this route writes <c>last_validated_at</c> only when
    /// the request carries no <c>Origin</c> header — and the response body is byte-identical either
    /// way, so a caller cannot tell that the write was skipped. That silence is expensive:
    /// <c>last_validated_at</c> is what moves a license out of <c>INACTIVE</c>, and it is the
    /// baseline the server's check-in-overdue sweep measures against, so a client that only ever
    /// quick-validates over an <c>Origin</c>-bearing transport keeps the license looking inactive
    /// and overdue forever.
    ///
    /// <see cref="AuthTransport.Cookie"/> is the one transport this SDK sends <c>Origin</c> on. So
    /// when it is configured, this method transparently issues
    /// <c>POST /licenses/{id}/actions/validate</c> instead (which has no <c>Origin</c> branch) and
    /// projects its <c>meta</c> onto the same <see cref="QuickValidationResult"/> shape — one
    /// request either way, same return type, and the timestamp actually gets written. Note that a
    /// proxy or middleware outside this SDK can add <c>Origin</c> to any transport; if that happens
    /// the fallback cannot fire, and only <see cref="ValidateByIdAsync"/> is safe.
    /// </remarks>
    public async Task<QuickValidationResult> QuickValidateAsync(Guid licenseId, CancellationToken cancellationToken = default)
    {
        if (Options.Auth is AuthTransport.Cookie)
        {
            var validated = await ValidateByIdAsync(licenseId, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new QuickValidationResult
            {
                Ts = validated.Meta.Ts,
                Valid = validated.Meta.Valid,
                Detail = validated.Meta.Detail,
                Code = validated.Meta.Code,
            };
        }

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
