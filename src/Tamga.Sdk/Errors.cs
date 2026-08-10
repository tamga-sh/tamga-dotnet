using System.Text.Json.Serialization;

namespace Tamga.Sdk;

/// <summary>The <c>source</c> object on a JSON:API error, pointing at the offending request field.</summary>
public sealed record TamgaApiErrorSource
{
    [JsonPropertyName("pointer")]
    public string? Pointer { get; init; }
}

/// <summary>
/// A single error object from a JSON:API <c>{"errors": [...]}</c> envelope
/// (<c>{ id, status, code, title, detail, source: { pointer } }</c>). Dispatch on
/// <see cref="Code"/> (stable) rather than <see cref="Detail"/> (human text, may change).
/// </summary>
public sealed record TamgaApiError
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("status")]
    public ushort Status { get; init; }

    [JsonPropertyName("code")]
    public string Code { get; init; } = "";

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("detail")]
    public string Detail { get; init; } = "";

    [JsonPropertyName("source")]
    public TamgaApiErrorSource? Source { get; init; }

    /// <summary>Convenience accessor for <c>Source.Pointer</c>.</summary>
    [JsonIgnore]
    public string? Pointer => Source?.Pointer;
}

/// <summary>The full JSON:API error envelope: <c>{"errors": [...]}</c>.</summary>
public sealed record TamgaApiErrorEnvelope
{
    [JsonPropertyName("errors")]
    public IReadOnlyList<TamgaApiError> Errors { get; init; } = Array.Empty<TamgaApiError>();
}

/// <summary>
/// Base type for every exception this SDK throws in response to a Tamga API error. Carries the
/// parsed <see cref="TamgaApiError"/> so callers can still inspect <see cref="TamgaApiError.Code"/>
/// even when they only catch the base type. Unmodeled <c>code</c> values map to this type directly
/// rather than a typed subclass.
/// </summary>
public class TamgaApiException : Exception
{
    public TamgaApiError Error { get; }

    public TamgaApiException(TamgaApiError error)
        : base($"Tamga API error {error.Code} ({error.Status}): {error.Detail}")
    {
        Error = error;
    }
}

/// <summary>
/// <c>422 CHECK_IN_NOT_REQUIRED</c> — a caller-logic error, not a retryable condition. Callers
/// should check <c>policy.RequireCheckIn</c> before scheduling periodic check-ins rather than
/// retry-looping on this exception.
/// </summary>
public sealed class CheckInNotRequiredException : TamgaApiException
{
    public CheckInNotRequiredException(TamgaApiError error) : base(error) { }
}

/// <summary>
/// <c>409 FINGERPRINT_TAKEN</c> — a machine or component fingerprint is already in use within its
/// unique scope (<c>(account_id, license_id, fingerprint)</c> for machines,
/// <c>(account_id, machine_id, fingerprint)</c> for components).
/// </summary>
public sealed class FingerprintTakenException : TamgaApiException
{
    public FingerprintTakenException(TamgaApiError error) : base(error) { }
}

/// <summary><c>409 PID_TAKEN</c> — a process PID is already in use on the target machine.</summary>
public sealed class PidTakenException : TamgaApiException
{
    public PidTakenException(TamgaApiError error) : base(error) { }
}

/// <summary><c>409 KEY_TAKEN</c> — the requested license key is already in use.</summary>
public sealed class KeyTakenException : TamgaApiException
{
    public KeyTakenException(TamgaApiError error) : base(error) { }
}

/// <summary>
/// <c>422 TTL_INVALID</c> — the requested checkout <c>ttl</c> is outside the server-accepted range
/// (<c>&gt; 0</c> and <c>&lt;= 31536000</c>, 365 days). The SDK also validates this client-side
/// before sending, to fail fast — see machine checkout.
/// </summary>
public sealed class TtlInvalidException : TamgaApiException
{
    public TtlInvalidException(TamgaApiError error) : base(error) { }
}

/// <summary><c>422 LICENSE_NOT_ENCRYPTED</c> — <c>encrypt=true</c> was requested but the license has no <c>key</c> set.</summary>
public sealed class LicenseNotEncryptedException : TamgaApiException
{
    public LicenseNotEncryptedException(TamgaApiError error) : base(error) { }
}

/// <summary><c>422 LICENSE_KEY_MISSING</c> — an operation required a license key that is not set.</summary>
public sealed class LicenseKeyMissingException : TamgaApiException
{
    public LicenseKeyMissingException(TamgaApiError error) : base(error) { }
}

/// <summary>
/// <c>422 SCHEME_NOT_SUPPORTED</c> — thrown both when the server rejects a machine-checkout
/// request for an unsupported scheme (currently <c>RSA_2048_JWT_RS256</c>), and client-side by
/// <c>MachineFile</c>'s verifier when asked to verify a file under that scheme —
/// the SDK must not silently no-op or attempt JWT/RS256 verification.
/// </summary>
public sealed class SchemeNotSupportedException : TamgaApiException
{
    public SchemeNotSupportedException(TamgaApiError error) : base(error) { }

    /// <summary>Constructs a purely client-side instance (no server round-trip occurred) for the local machine-file-verify rejection path.</summary>
    public SchemeNotSupportedException(string detail)
        : base(new TamgaApiError { Status = 422, Code = "SCHEME_NOT_SUPPORTED", Detail = detail })
    {
    }
}

/// <summary><c>422 DATASET_INVALID</c> — the <c>dataset</c> object supplied to offline-proof generation was rejected.</summary>
public sealed class DatasetInvalidException : TamgaApiException
{
    public DatasetInvalidException(TamgaApiError error) : base(error) { }
}

/// <summary>
/// Thrown client-side (never from a server response) when a <c>.lic</c>/<c>.machine</c> file's
/// <c>alg</c> field, or a proof's version prefix, is not one this SDK recognizes. Deliberately a
/// typed error rather than a silent no-op — see <see cref="Checkout.LicenseFile"/>/
/// <c>MachineFile</c>.
/// </summary>
public sealed class UnsupportedAlgorithmException : Exception
{
    public UnsupportedAlgorithmException(string message) : base(message) { }
}

/// <summary>
/// Thrown client-side when a <c>.lic</c>/<c>.machine</c> PEM envelope or its inner JSON is
/// malformed (missing markers, invalid base64, invalid JSON shape).
/// </summary>
public sealed class OfflineFileFormatException : Exception
{
    public OfflineFileFormatException(string message) : base(message) { }
}

/// <summary>Thrown when Ed25519/RSA/ECDSA signature verification fails on a <c>.lic</c>/<c>.machine</c> file or offline proof — always fails closed, never silently accepts.</summary>
public sealed class SignatureVerificationException : Exception
{
    public SignatureVerificationException(string message) : base(message) { }
}

/// <summary><c>404 NOT_FOUND</c>.</summary>
public sealed class TamgaNotFoundException : TamgaApiException
{
    public TamgaNotFoundException(TamgaApiError error) : base(error) { }
}

/// <summary><c>401 UNAUTHORIZED</c>.</summary>
public sealed class TamgaUnauthorizedException : TamgaApiException
{
    public TamgaUnauthorizedException(TamgaApiError error) : base(error) { }
}

/// <summary><c>403 FORBIDDEN</c>.</summary>
public sealed class TamgaForbiddenException : TamgaApiException
{
    public TamgaForbiddenException(TamgaApiError error) : base(error) { }
}

/// <summary>
/// <c>500 INTERNAL_SERVER_ERROR</c> — generic. Never assume <see cref="TamgaApiError.Detail"/>
/// carries parseable/leaked DB detail on this code.
/// </summary>
public sealed class TamgaInternalServerErrorException : TamgaApiException
{
    public TamgaInternalServerErrorException(TamgaApiError error) : base(error) { }
}

/// <summary>
/// Maps a parsed <see cref="TamgaApiError"/> to its typed exception. Dispatch is on
/// <see cref="TamgaApiError.Code"/> only.
/// </summary>
/// <remarks>
/// GOTCHA: <c>429 TOO_MANY_REQUESTS</c> is declared in the server's error enum but has no
/// constructor and is never returned by any code path today — deliberately not mapped to a typed
/// exception here, and no client-side 429/backoff handling exists anywhere in this SDK.
/// </remarks>
public static class TamgaErrorMapper
{
    public static TamgaApiException ToException(TamgaApiError error) => error.Code switch
    {
        "CHECK_IN_NOT_REQUIRED" => new CheckInNotRequiredException(error),
        "FINGERPRINT_TAKEN" => new FingerprintTakenException(error),
        "PID_TAKEN" => new PidTakenException(error),
        "KEY_TAKEN" => new KeyTakenException(error),
        "TTL_INVALID" => new TtlInvalidException(error),
        "LICENSE_NOT_ENCRYPTED" => new LicenseNotEncryptedException(error),
        "LICENSE_KEY_MISSING" => new LicenseKeyMissingException(error),
        "SCHEME_NOT_SUPPORTED" => new SchemeNotSupportedException(error),
        "DATASET_INVALID" => new DatasetInvalidException(error),
        "NOT_FOUND" => new TamgaNotFoundException(error),
        "UNAUTHORIZED" => new TamgaUnauthorizedException(error),
        "FORBIDDEN" => new TamgaForbiddenException(error),
        "INTERNAL_SERVER_ERROR" => new TamgaInternalServerErrorException(error),
        _ => new TamgaApiException(error),
    };
}
