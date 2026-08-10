using System.Text.Json.Serialization;
using Tamga.Sdk.Checkout;

namespace Tamga.Sdk;

/// <summary>Request body shared by both license and machine checkout: <c>{ "meta": { "encrypt": bool, "ttl": int } }</c>.</summary>
public sealed record CheckOutRequestMeta
{
    /// <summary>Whether the checked-out file's key material should be encrypted.</summary>
    [JsonPropertyName("encrypt")]
    public bool Encrypt { get; init; }

    /// <summary>The requested time-to-live for the checked-out file, in seconds.</summary>
    [JsonPropertyName("ttl")]
    public int? Ttl { get; init; }
}

/// <summary>Request body for <c>POST .../actions/check-out</c> (both license and machine).</summary>
public sealed record CheckOutRequest
{
    /// <summary>The check-out request's <c>meta</c> options.</summary>
    [JsonPropertyName("meta")]
    public required CheckOutRequestMeta Meta { get; init; }
}

/// <summary>
/// The JSON:API <c>license-files</c>/<c>machine-files</c> resource attributes returned by the
/// <c>POST .../actions/check-out</c> variant: <c>{ certificate, algorithm, includes, ttl, expiry, issued }</c>.
/// </summary>
public sealed record CheckoutFileAttributes
{
    /// <summary>The full PEM-wrapped <c>.lic</c>/<c>.machine</c> file content.</summary>
    [JsonPropertyName("certificate")]
    public string Certificate { get; init; } = "";

    /// <summary>The signing/encryption algorithm used to produce the certificate.</summary>
    [JsonPropertyName("algorithm")]
    public string Algorithm { get; init; } = "";

    /// <summary>GOTCHA: always <c>[]</c> server-side — there is no working <c>include[]</c> feature despite this field existing.</summary>
    [JsonPropertyName("includes")]
    public string[]? Includes { get; init; }

    /// <summary>
    /// GOTCHA: metadata only — NOT embedded in the signed payload and NOT re-checked server-side
    /// on later validation. Offline-file expiry enforcement is entirely this SDK's client-side
    /// responsibility.
    /// </summary>
    [JsonPropertyName("ttl")]
    public int? Ttl { get; init; }

    /// <summary>When the checked-out file expires.</summary>
    [JsonPropertyName("expiry")]
    public DateTimeOffset? Expiry { get; init; }

    /// <summary>When the checked-out file was issued.</summary>
    [JsonPropertyName("issued")]
    public DateTimeOffset? Issued { get; init; }
}

public sealed partial class TamgaClient
{
    // ---------------------------------------------------------------
    // §E License Checkout Crypto
    //
    // GOTCHA: checkout `id` is a fresh UUIDv7 per call, not idempotent — calling checkout twice
    // yields two different certificates. GOTCHA: server returns 422 LICENSE_NOT_ENCRYPTED when the
    // license has no `key` set and encrypt=true is requested (surfaced as
    // LicenseNotEncryptedException via TamgaErrorMapper).
    //
    // This SDK uses the POST .../actions/check-out variant (JSON:API `license-files` resource)
    // rather than the raw-octet-stream GET variant, since the POST response also carries
    // ttl/expiry/issued metadata this SDK needs for client-side offline-expiry enforcement.
    // ---------------------------------------------------------------

    /// <summary>
    /// <c>POST /licenses/{license_id}/actions/check-out</c> — checks out an offline <c>.lic</c>
    /// file. Returns the parsed, UNVERIFIED <see cref="LicenseFile"/> — call
    /// <see cref="LicenseFile.Verify"/> or <see cref="LicenseFile.VerifyAndDecrypt"/> separately.
    /// </summary>
    public async Task<LicenseFile> CheckOutLicenseAsync(
        Guid licenseId,
        bool encrypt = false,
        int? ttl = null,
        CancellationToken cancellationToken = default)
    {
        var doc = await _transport.SendJsonApiAsync<CheckoutFileAttributes>(
            HttpMethod.Post,
            $"/licenses/{licenseId}/actions/check-out",
            jsonBody: new CheckOutRequest { Meta = new CheckOutRequestMeta { Encrypt = encrypt, Ttl = ttl } },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var certificate = doc.Data?.Attributes?.Certificate;
        if (string.IsNullOrEmpty(certificate))
        {
            throw new TamgaApiException(new TamgaApiError { Status = 200, Code = "MISSING_CERTIFICATE", Detail = "Checkout response had no certificate." });
        }

        return LicenseFile.Parse(certificate);
    }

    // ---------------------------------------------------------------
    // §F Machine Checkout Crypto
    // ---------------------------------------------------------------

    /// <summary>
    /// <c>POST /machines/{id}/actions/check-out</c> — checks out an offline <c>.machine</c> file.
    /// Validates <paramref name="ttl"/> client-side (mirroring the server's
    /// <c>&gt; 0 &amp;&amp; &lt;= 31536000</c> check) before sending, to fail fast. Returns the
    /// parsed, UNVERIFIED <see cref="MachineFile"/>.
    /// </summary>
    /// <exception cref="TtlInvalidException"><paramref name="ttl"/> is outside the valid range.</exception>
    public async Task<MachineFile> CheckOutMachineAsync(
        Guid machineId,
        bool encrypt = false,
        int? ttl = null,
        CancellationToken cancellationToken = default)
    {
        if (ttl is int t)
        {
            MachineFile.ValidateTtl(t);
        }

        var doc = await _transport.SendJsonApiAsync<CheckoutFileAttributes>(
            HttpMethod.Post,
            $"/machines/{machineId}/actions/check-out",
            jsonBody: new CheckOutRequest { Meta = new CheckOutRequestMeta { Encrypt = encrypt, Ttl = ttl } },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var certificate = doc.Data?.Attributes?.Certificate;
        if (string.IsNullOrEmpty(certificate))
        {
            throw new TamgaApiException(new TamgaApiError { Status = 200, Code = "MISSING_CERTIFICATE", Detail = "Checkout response had no certificate." });
        }

        return MachineFile.Parse(certificate);
    }
}
