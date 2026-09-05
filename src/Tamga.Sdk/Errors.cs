using System.Text.Json;
using System.Text.Json.Serialization;
using Tamga.Sdk.Models;

namespace Tamga.Sdk;

/// <summary>The <c>source</c> object on a JSON:API error, pointing at the offending request field.</summary>
public sealed record TamgaApiErrorSource
{
    /// <summary>JSON Pointer to the offending request field, e.g. <c>/data/attributes/key</c>.</summary>
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
    /// <summary>Server-assigned identifier for this error occurrence.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>The HTTP status code associated with this error.</summary>
    /// <remarks>
    /// The server sends this as a JSON <em>string</em> (<c>"status": "422"</c>, from
    /// <c>status.as_u16().to_string()</c>), not a number. The
    /// <see cref="JsonNumberHandlingAttribute"/> on this property is what binds that string to a
    /// <see cref="ushort"/>, so the envelope decodes under <em>any</em>
    /// <see cref="JsonSerializerOptions"/> — not only <see cref="TamgaJsonOptions.Default"/>, which
    /// used to be the single option set it worked under (audit D18). A JSON number binds too.
    /// Prefer <see cref="Code"/> for dispatch regardless; the status only narrows the class of failure.
    /// </remarks>
    [JsonPropertyName("status")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public ushort Status { get; init; }

    /// <summary>Stable machine-readable error code — dispatch on this, not <see cref="Detail"/>.</summary>
    [JsonPropertyName("code")]
    public string Code { get; init; } = "";

    /// <summary>Short, human-readable summary of the error type.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Human-readable explanation specific to this error occurrence.</summary>
    [JsonPropertyName("detail")]
    public string Detail { get; init; } = "";

    /// <summary>The request field that caused this error, if applicable.</summary>
    [JsonPropertyName("source")]
    public TamgaApiErrorSource? Source { get; init; }

    /// <summary>
    /// The error's <c>meta</c> object, verbatim, or <see langword="null"/> when the server sent
    /// none.
    /// </summary>
    /// <remarks>
    /// Exactly one error carries a <c>meta</c> today: <c>409 FINGERPRINT_TAKEN</c> from
    /// <c>POST /machines</c> sends <c>{"machineId": "&lt;uuid&gt;"}</c>, and only when the machine
    /// holding the fingerprint is on the license named in the request. A conflict with a machine
    /// on another license (under <c>UNIQUE_PER_POLICY</c> / <c>UNIQUE_PER_ACCOUNT</c>) carries no
    /// <c>meta</c> at all. <see cref="FingerprintTakenException.ExistingMachineId"/> reads it.
    /// Kept as a <see cref="JsonElement"/> rather than a typed record so a future error's
    /// <c>meta</c> binds without an SDK change. It round-trips through
    /// <see cref="TamgaJsonOptions.Default"/> (omitted when null). The raw-body recovery path in
    /// <see cref="TamgaTransport"/> does not reconstruct it: an envelope that failed to bind yields
    /// an exception whose <c>Meta</c> is <see langword="null"/>.
    /// </remarks>
    [JsonPropertyName("meta")]
    public JsonElement? Meta { get; init; }

    /// <summary>Convenience accessor for <c>Source.Pointer</c>.</summary>
    [JsonIgnore]
    public string? Pointer => Source?.Pointer;
}

/// <summary>The full JSON:API error envelope: <c>{"errors": [...]}</c>.</summary>
public sealed record TamgaApiErrorEnvelope
{
    /// <summary>The list of errors returned by the API.</summary>
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
    /// <summary>The parsed API error that caused this exception.</summary>
    public TamgaApiError Error { get; }

    /// <summary>
    /// The failure that stopped the server's error envelope from binding, when
    /// <see cref="Error"/> had to be recovered from the raw response body instead.
    /// <see langword="null"/> on the normal path.
    /// </summary>
    /// <remarks>
    /// Exists so a malformed envelope is diagnosable rather than silent. It used to be swallowed
    /// outright, and the recovery path then overwrote the server's <c>code</c> with the HTTP
    /// status name — so the one thing a caller could dispatch on was destroyed and the reason was
    /// gone too. This is diagnostic only: dispatch on <see cref="TamgaApiError.Code"/>.
    /// </remarks>
    /// <value>
    /// The same object is also chained as this exception's <see cref="Exception.InnerException"/>,
    /// so it shows up in <see cref="Exception.ToString"/> and in any logging or APM tooling that
    /// walks the inner-exception chain without knowing about this SDK.
    /// </value>
    public Exception? ErrorBodyParseFailure { get; }

    /// <summary>Constructs from a parsed API error.</summary>
    public TamgaApiException(TamgaApiError error)
        : this(error, null)
    {
    }

    /// <remarks>
    /// The parse failure is chained through <see cref="Exception.InnerException"/> as well as
    /// exposed on <see cref="ErrorBodyParseFailure"/>. Both channels are required: the typed
    /// property is what SDK-aware code reads, while <c>InnerException</c> is what
    /// <see cref="Exception.ToString"/>, <c>ILogger</c> sinks and APM agents walk automatically —
    /// setting only the property would leave the diagnostic invisible to every generic tool.
    /// </remarks>
    internal TamgaApiException(TamgaApiError error, Exception? errorBodyParseFailure)
        : base($"Tamga API error {error.Code} ({error.Status}): {error.Detail}", errorBodyParseFailure)
    {
        Error = error;
        ErrorBodyParseFailure = errorBodyParseFailure;
    }
}

/// <summary>
/// <c>422 CHECK_IN_NOT_REQUIRED</c> — a caller-logic error, not a retryable condition. Callers
/// should check <c>policy.RequireCheckIn</c> before scheduling periodic check-ins rather than
/// retry-looping on this exception.
/// </summary>
public sealed class CheckInNotRequiredException : TamgaApiException
{
    /// <summary>Constructs from a parsed API error.</summary>
    public CheckInNotRequiredException(TamgaApiError error) : base(error) { }

    internal CheckInNotRequiredException(TamgaApiError error, Exception? errorBodyParseFailure) : base(error, errorBodyParseFailure) { }
}

/// <summary>
/// <c>409 FINGERPRINT_TAKEN</c> — a machine or component fingerprint is already in use within its
/// unique scope (<c>(account_id, license_id, fingerprint)</c> for machines,
/// <c>(account_id, machine_id, fingerprint)</c> for components).
/// </summary>
public sealed class FingerprintTakenException : TamgaApiException
{
    /// <summary>Constructs from a parsed API error.</summary>
    public FingerprintTakenException(TamgaApiError error) : base(error)
        => ExistingMachineId = ReadExistingMachineId(error);

    internal FingerprintTakenException(TamgaApiError error, Exception? errorBodyParseFailure) : base(error, errorBodyParseFailure)
        => ExistingMachineId = ReadExistingMachineId(error);

    /// <summary>
    /// The id of the machine that already holds the fingerprint, when the server named it in
    /// <c>meta.machineId</c>; otherwise <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// The server names it exactly when that machine is on the license the request asked for, so
    /// a non-null value is safe to adopt — <see cref="TamgaClient.ActivateMachineIdempotentAsync"/>
    /// reads it by id instead of searching. <see langword="null"/> means one of: an older server
    /// that sends no <c>meta</c>; a cross-license conflict (never carries one); a component
    /// conflict; or a <c>meta</c> that did not hold a UUID string under <c>machineId</c>. None of
    /// those throw — the conflict itself is still the information.
    /// </remarks>
    public Guid? ExistingMachineId { get; }

    private static Guid? ReadExistingMachineId(TamgaApiError error) =>
        error.Meta is { ValueKind: JsonValueKind.Object } meta
        && meta.TryGetProperty("machineId", out var id)
        && id.ValueKind == JsonValueKind.String
        && Guid.TryParse(id.GetString(), out var machineId)
            ? machineId
            : null;
}

/// <summary><c>409 PID_TAKEN</c> — a process PID is already in use on the target machine.</summary>
public sealed class PidTakenException : TamgaApiException
{
    /// <summary>Constructs from a parsed API error.</summary>
    public PidTakenException(TamgaApiError error) : base(error) { }

    internal PidTakenException(TamgaApiError error, Exception? errorBodyParseFailure) : base(error, errorBodyParseFailure) { }
}

/// <summary><c>409 KEY_TAKEN</c> — the requested license key is already in use.</summary>
public sealed class KeyTakenException : TamgaApiException
{
    /// <summary>Constructs from a parsed API error.</summary>
    public KeyTakenException(TamgaApiError error) : base(error) { }

    internal KeyTakenException(TamgaApiError error, Exception? errorBodyParseFailure) : base(error, errorBodyParseFailure) { }
}

/// <summary>
/// <c>422 TTL_INVALID</c> — the requested checkout <c>ttl</c> is outside the server-accepted range
/// (<c>&gt; 0</c> and <c>&lt;= 31536000</c>, 365 days). The SDK also validates this client-side
/// before sending, to fail fast — see machine checkout.
/// </summary>
public sealed class TtlInvalidException : TamgaApiException
{
    /// <summary>Constructs from a parsed API error.</summary>
    public TtlInvalidException(TamgaApiError error) : base(error) { }

    internal TtlInvalidException(TamgaApiError error, Exception? errorBodyParseFailure) : base(error, errorBodyParseFailure) { }
}

/// <summary><c>422 LICENSE_NOT_ENCRYPTED</c> — <c>encrypt=true</c> was requested but the license has no <c>key</c> set.</summary>
public sealed class LicenseNotEncryptedException : TamgaApiException
{
    /// <summary>Constructs from a parsed API error.</summary>
    public LicenseNotEncryptedException(TamgaApiError error) : base(error) { }

    internal LicenseNotEncryptedException(TamgaApiError error, Exception? errorBodyParseFailure) : base(error, errorBodyParseFailure) { }
}

/// <summary><c>422 LICENSE_KEY_MISSING</c> — an operation required a license key that is not set.</summary>
public sealed class LicenseKeyMissingException : TamgaApiException
{
    /// <summary>Constructs from a parsed API error.</summary>
    public LicenseKeyMissingException(TamgaApiError error) : base(error) { }

    internal LicenseKeyMissingException(TamgaApiError error, Exception? errorBodyParseFailure) : base(error, errorBodyParseFailure) { }
}

/// <summary>
/// <c>422 SCHEME_NOT_SUPPORTED</c> — thrown both when the server rejects a machine-checkout
/// request for an unsupported scheme (currently <c>RSA_2048_JWT_RS256</c>), and client-side by
/// <c>MachineFile</c>'s verifier when asked to verify a file under that scheme —
/// the SDK must not silently no-op or attempt JWT/RS256 verification.
/// </summary>
public sealed class SchemeNotSupportedException : TamgaApiException
{
    /// <summary>Constructs from a parsed API error.</summary>
    public SchemeNotSupportedException(TamgaApiError error) : base(error) { }

    internal SchemeNotSupportedException(TamgaApiError error, Exception? errorBodyParseFailure) : base(error, errorBodyParseFailure) { }

    /// <summary>Constructs a purely client-side instance (no server round-trip occurred) for the local machine-file-verify rejection path.</summary>
    public SchemeNotSupportedException(string detail)
        : base(new TamgaApiError { Status = 422, Code = "SCHEME_NOT_SUPPORTED", Detail = detail })
    {
    }
}

/// <summary>
/// <c>422 SIGNING_KEY_MISSING</c> — the account has no Ed25519 signing key, so the server cannot
/// sign what was asked of it: a license check-out, a machine check-out, or an offline proof.
/// </summary>
/// <remarks>
/// Not retryable and not fixable from the client: the account's key has to be populated
/// server-side. Every account created after the server's key-set backfill has one from creation,
/// and the startup sweep backfills the accounts that predate it, so this is expected only against
/// a server that has not run that sweep. Those routes used to answer <c>500</c> for the same
/// condition; the typed <c>422</c> is what a client can act on.
/// </remarks>
public sealed class SigningKeyMissingException : TamgaApiException
{
    /// <summary>Constructs from a parsed API error.</summary>
    public SigningKeyMissingException(TamgaApiError error) : base(error) { }

    internal SigningKeyMissingException(TamgaApiError error, Exception? errorBodyParseFailure) : base(error, errorBodyParseFailure) { }
}

/// <summary>
/// <c>422 SECRET_KEY_MISSING</c> — the account has no secret key, so the server cannot mint a
/// token.
/// </summary>
/// <remarks>Same class of failure as <see cref="SigningKeyMissingException"/>: an account-level precondition, fixed server-side, never by retrying.</remarks>
public sealed class SecretKeyMissingException : TamgaApiException
{
    /// <summary>Constructs from a parsed API error.</summary>
    public SecretKeyMissingException(TamgaApiError error) : base(error) { }

    internal SecretKeyMissingException(TamgaApiError error, Exception? errorBodyParseFailure) : base(error, errorBodyParseFailure) { }
}

/// <summary><c>422 DATASET_INVALID</c> — the <c>dataset</c> object supplied to offline-proof generation was rejected.</summary>
public sealed class DatasetInvalidException : TamgaApiException
{
    /// <summary>Constructs from a parsed API error.</summary>
    public DatasetInvalidException(TamgaApiError error) : base(error) { }

    internal DatasetInvalidException(TamgaApiError error, Exception? errorBodyParseFailure) : base(error, errorBodyParseFailure) { }
}

/// <summary>
/// Thrown client-side (never from a server response) when a <c>.lic</c>/<c>.machine</c> file's
/// <c>alg</c> field, or a proof's version prefix, is not one this SDK recognizes. Deliberately a
/// typed error rather than a silent no-op — see <see cref="Checkout.LicenseFile"/>/
/// <c>MachineFile</c>.
/// </summary>
public sealed class UnsupportedAlgorithmException : Exception
{
    /// <summary>Constructs with the given error message.</summary>
    public UnsupportedAlgorithmException(string message) : base(message) { }
}

/// <summary>
/// Thrown when an offline file's signature verified but its signed <c>exp</c> claim has
/// passed — an authentic file that has simply run out.
/// </summary>
/// <remarks>
/// Its own type on purpose: a caller that cannot tell "expired" from "forged" either warns the
/// user about tampering when their trial merely ended, or treats a forgery as a renewal prompt.
///
/// Raised by BOTH offline file formats — <see cref="Checkout.LicenseFile"/> and
/// <see cref="Checkout.MachineFile"/> — which carry the same signed
/// <see cref="Models.LicenseFileClaims"/> shape and share one clock-skew tolerance. The name
/// keeps its <c>LicenseFile</c> prefix for source compatibility; the message it builds says
/// "License file" for the same reason. Catch it on a machine-file verify too.
/// </remarks>
public sealed class LicenseFileExpiredException : Exception
{
    /// <summary>The <c>exp</c> claim, seconds since the Unix epoch.</summary>
    public long ExpiresAt { get; }

    /// <summary>Constructs the exception for a file that expired at <paramref name="expiresAt"/>.</summary>
    public LicenseFileExpiredException(long expiresAt)
        : base($"License file expired at unix timestamp {expiresAt}.")
        => ExpiresAt = expiresAt;
}

/// <summary>
/// Thrown client-side when a <c>.lic</c>/<c>.machine</c> PEM envelope or its inner JSON is
/// malformed (missing markers, invalid base64, invalid JSON shape).
/// </summary>
public sealed class OfflineFileFormatException : Exception
{
    /// <summary>Constructs with the given error message.</summary>
    public OfflineFileFormatException(string message) : base(message) { }
}

/// <summary>Thrown when Ed25519/RSA/ECDSA signature verification fails on a <c>.lic</c>/<c>.machine</c> file or offline proof — always fails closed, never silently accepts.</summary>
public sealed class SignatureVerificationException : Exception
{
    /// <summary>Constructs with the given error message.</summary>
    public SignatureVerificationException(string message) : base(message) { }
}

/// <summary>
/// Base class for every failure to <em>select</em> a signing key for an offline file — as opposed
/// to <see cref="SignatureVerificationException"/>, which means a key was selected and the
/// signature still did not check out.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keeping these apart is the entire point of the key-set entry points.</b> They are different
/// incidents with opposite responses:
/// </para>
/// <list type="bullet">
/// <item><description>
/// A <see cref="SigningKeySelectionException"/> means <b>the file is not necessarily bad</b> — the
/// key that signed it simply is not in the set. Refresh the key set or ship an application update,
/// then try again. Locking the customer out here is the bug this exists to prevent.
/// </description></item>
/// <item><description>
/// A <see cref="SignatureVerificationException"/> from a key-set entry point means the file's
/// <c>kid</c> named a key the caller <em>does</em> trust and the signature still failed. That file
/// is forged or corrupted. Refuse it.
/// </description></item>
/// </list>
/// <para>
/// Catch this base type to handle "I cannot verify this with the keys I have" uniformly, or the
/// sealed subclasses to tell a stale key set from a server that never published one.
/// </para>
/// </remarks>
public abstract class SigningKeySelectionException : Exception
{
    /// <summary>Constructs with the given message, the file's <c>kid</c>, and the ids that were available.</summary>
    private protected SigningKeySelectionException(string message, string keyId, IReadOnlyList<string> availableKeyIds)
        : base(message)
    {
        KeyId = keyId;
        AvailableKeyIds = availableKeyIds;
    }

    /// <summary>
    /// The <c>kid</c> the file claims, verbatim.
    /// </summary>
    /// <remarks>
    /// Unverified input, and treated as such: it is read from bytes whose signature has not been
    /// checked, and the only thing ever done with it is to select from keys the caller already
    /// supplied. It can never introduce a key. Safe to log; do not derive anything else from it.
    /// </remarks>
    public string KeyId { get; }

    /// <summary>The usable <c>kid</c>s the supplied key set did hold. Log beside <see cref="KeyId"/>.</summary>
    public IReadOnlyList<string> AvailableKeyIds { get; }
}

/// <summary>
/// The file names a signing key that is not in the supplied key set.
/// </summary>
/// <remarks>
/// <b>This is the case that is not a forgery.</b> The file says which key signed it, and that key
/// is simply absent — a set fetched before the last rotation, a pinned key that has since been
/// superseded, or a key an operator deleted outright (which is how a <em>compromised</em> key is
/// retired, and which does invalidate every legitimate file signed with it). Fetch the key set
/// again, or ship an update carrying the new pinned key, before treating the file as suspect.
/// </remarks>
public sealed class UnknownSigningKeyException : SigningKeySelectionException
{
    /// <summary>Constructs from the file's <c>kid</c> and the ids the key set held.</summary>
    public UnknownSigningKeyException(string keyId, IReadOnlyList<string> availableKeyIds)
        : base(
            $"The file is signed by key '{keyId}', which is not in the supplied key set" +
            (availableKeyIds.Count == 0
                ? " (the key set held no usable key). This is not a signature failure — refresh the key set before treating the file as forged."
                : $" (had: {string.Join(", ", availableKeyIds)}). This is not a signature failure — refresh the key set before treating the file as forged."),
            keyId,
            availableKeyIds)
    {
    }
}

/// <summary>
/// The file was signed by an account whose Ed25519 public key was never populated server-side, so
/// its <c>kid</c> is <see cref="Crypto.Ed25519.UnpublishedAccountKeyId"/> and no key set can ever
/// contain it.
/// </summary>
/// <remarks>
/// <para>
/// Distinguished from <see cref="UnknownSigningKeyException"/> because the remedy is completely
/// different, and "refresh your key set" would send the caller — and support — somewhere that
/// cannot help. Nothing on the client side can fix this one.
/// </para>
/// <para>
/// Both checkout handlers compute the claim as
/// <c>key_id(account.ed25519_public_key.as_deref().unwrap_or_default())</c>
/// (<c>check_out_license.rs:95-97</c>, <c>check_out_machine.rs:127-129</c>). An account whose key
/// column was never backfilled therefore signs <b>every</b> file it issues with
/// <c>SHA-256("")</c> truncated — the constant <c>e3b0c44298fc1c14</c>. Since the empty string is
/// not a valid public key, no published key set can hold that id, so the condition is permanent
/// until the account's key is populated server-side.
/// </para>
/// </remarks>
public sealed class UnpublishedSigningKeyException : SigningKeySelectionException
{
    /// <summary>Constructs from the file's <c>kid</c> and the ids the key set held.</summary>
    public UnpublishedSigningKeyException(string keyId, IReadOnlyList<string> availableKeyIds)
        : base(
            $"The file's 'kid' is '{keyId}', the id of the empty string — the signing account has no Ed25519 public key " +
            "recorded server-side, so it signs every file it issues with this one id and no key set can contain it. " +
            "This is a server-side account configuration problem, not a stale key set and not a forged file.",
            keyId,
            availableKeyIds)
    {
    }
}

/// <summary>
/// The file cannot be matched to a key by <c>kid</c> at all, because for its signing scheme the
/// <c>kid</c> does not name the key that signed it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a server property, not a client limitation.</b> Both checkout handlers compute the
/// claim as <c>key_id(account.ed25519_public_key…)</c> unconditionally
/// (<c>check_out_machine.rs:127-129</c>), while a machine file's <em>signing</em> key is chosen by
/// the license's scheme (<c>check_out_machine.rs:86-99</c> selects the RSA, ECDSA or Ed25519
/// private key). For an RSA- or ECDSA-signed machine file those are different keys, so the
/// <c>kid</c> names an Ed25519 key that had no part in the signature — and the key-set endpoint
/// publishes Ed25519 keys only in any case.
/// </para>
/// <para>
/// License files are unaffected: they are always Ed25519-signed, so their <c>kid</c> always names
/// their signing key.
/// </para>
/// <para>
/// Verify these with the scheme-taking
/// <see cref="Checkout.MachineFile.VerifyWithClaims(Models.LicenseScheme, ReadOnlySpan{byte}, string, string, long)"/>
/// and the account's own public key for that algorithm. Rotation only ever rotates the Ed25519 key
/// (<c>rotate_ed25519</c>), so there is no rotation for these schemes to survive today.
/// </para>
/// </remarks>
public sealed class SigningKeyNotApplicableException : SigningKeySelectionException
{
    /// <summary>Constructs from the offending scheme.</summary>
    public SigningKeyNotApplicableException(Models.LicenseScheme scheme)
        : base(
            $"A {scheme} machine file's 'kid' claim names the account's Ed25519 key, not the key that signed it, " +
            "so it cannot be matched against a key set. Verify it with the scheme-taking overload and that scheme's public key instead.",
            keyId: "",
            availableKeyIds: Array.Empty<string>())
    {
        Scheme = scheme;
    }

    /// <summary>The scheme whose machine files carry a <c>kid</c> that does not name their signing key.</summary>
    public Models.LicenseScheme Scheme { get; }
}

/// <summary><c>404 NOT_FOUND</c>.</summary>
public sealed class TamgaNotFoundException : TamgaApiException
{
    /// <summary>Constructs from a parsed API error.</summary>
    public TamgaNotFoundException(TamgaApiError error) : base(error) { }

    internal TamgaNotFoundException(TamgaApiError error, Exception? errorBodyParseFailure) : base(error, errorBodyParseFailure) { }
}

/// <summary><c>401 UNAUTHORIZED</c>.</summary>
public sealed class TamgaUnauthorizedException : TamgaApiException
{
    /// <summary>Constructs from a parsed API error.</summary>
    public TamgaUnauthorizedException(TamgaApiError error) : base(error) { }

    internal TamgaUnauthorizedException(TamgaApiError error, Exception? errorBodyParseFailure) : base(error, errorBodyParseFailure) { }
}

/// <summary><c>403 FORBIDDEN</c>.</summary>
public sealed class TamgaForbiddenException : TamgaApiException
{
    /// <summary>Constructs from a parsed API error.</summary>
    public TamgaForbiddenException(TamgaApiError error) : base(error) { }

    internal TamgaForbiddenException(TamgaApiError error, Exception? errorBodyParseFailure) : base(error, errorBodyParseFailure) { }
}

/// <summary>
/// <c>500 INTERNAL_SERVER_ERROR</c> — generic. Never assume <see cref="TamgaApiError.Detail"/>
/// carries parseable/leaked DB detail on this code.
/// </summary>
public sealed class TamgaInternalServerErrorException : TamgaApiException
{
    /// <summary>Constructs from a parsed API error.</summary>
    public TamgaInternalServerErrorException(TamgaApiError error) : base(error) { }

    internal TamgaInternalServerErrorException(TamgaApiError error, Exception? errorBodyParseFailure) : base(error, errorBodyParseFailure) { }
}

/// <summary>
/// Base type for the <c>422</c> quota rejections the server raises at <em>creation</em> time —
/// machine activation and process spawn.
/// </summary>
/// <remarks>
/// These used to be reported only by a later <c>validate</c> call, which a client is free never to
/// make, so nothing actually stopped an over-limit activation. The server now checks the quota
/// inside the create transaction, which means <c>POST /machines</c> and <c>POST /processes</c> can
/// fail outright with a limit code instead of succeeding and leaving the overage for validation to
/// discover.
///
/// The two paths are not redundant, and neither replaces the other: the create-time check runs
/// through the policy's overage strategy, so under <c>ALLOW_ACCESS</c> or
/// <c>ALLOW_1_25X_OVERAGE</c> the create still succeeds and the limit surfaces only at validate.
/// <see cref="EquivalentValidationCode"/> normalizes the two so a caller can dispatch on one value
/// whichever path it arrived by — see <see cref="TamgaClient.ActivateMachineAsync"/>.
/// </remarks>
public abstract class TamgaLimitExceededException : TamgaApiException
{
    /// <summary>The <see cref="ValidationCode"/> a later <c>validate</c> call would report for this same overage.</summary>
    public ValidationCode EquivalentValidationCode { get; }

    /// <summary>Constructs from a parsed API error and the validate-time code it corresponds to.</summary>
    protected TamgaLimitExceededException(TamgaApiError error, ValidationCode equivalentValidationCode)
        : this(error, equivalentValidationCode, null)
    {
    }

    internal TamgaLimitExceededException(TamgaApiError error, ValidationCode equivalentValidationCode, Exception? errorBodyParseFailure)
        : base(error, errorBodyParseFailure)
        => EquivalentValidationCode = equivalentValidationCode;
}

/// <summary><c>422 MACHINE_LIMIT_EXCEEDED</c> — machine activation refused at creation time. Validate-time equivalent: <see cref="ValidationCode.TooManyMachines"/>.</summary>
public sealed class MachineLimitExceededException : TamgaLimitExceededException
{
    /// <summary>Constructs from a parsed API error.</summary>
    public MachineLimitExceededException(TamgaApiError error) : base(error, ValidationCode.TooManyMachines) { }

    internal MachineLimitExceededException(TamgaApiError error, Exception? errorBodyParseFailure) : base(error, ValidationCode.TooManyMachines, errorBodyParseFailure) { }
}

/// <summary><c>422 CORE_LIMIT_EXCEEDED</c> — CPU-core quota refused at creation time. Validate-time equivalent: <see cref="ValidationCode.TooManyCores"/>.</summary>
public sealed class CoreLimitExceededException : TamgaLimitExceededException
{
    /// <summary>Constructs from a parsed API error.</summary>
    public CoreLimitExceededException(TamgaApiError error) : base(error, ValidationCode.TooManyCores) { }

    internal CoreLimitExceededException(TamgaApiError error, Exception? errorBodyParseFailure) : base(error, ValidationCode.TooManyCores, errorBodyParseFailure) { }
}

/// <summary>
/// <c>422 MEMORY_LIMIT_EXCEEDED</c> — memory quota refused at creation time. Validate-time
/// equivalent: <see cref="ValidationCode.TooMuchMemory"/>.
/// </summary>
/// <remarks>
/// The quota is counted in <em>megabytes</em>. A caller that reports 16 GB as
/// <c>17179869184</c> instead of <c>16384</c> inflates the license's running total by a factor of
/// 1,048,576 and trips this on the next activation against the same license — see
/// <see cref="Models.CreateMachineRequest.Memory"/>.
/// </remarks>
public sealed class MemoryLimitExceededException : TamgaLimitExceededException
{
    /// <summary>Constructs from a parsed API error.</summary>
    public MemoryLimitExceededException(TamgaApiError error) : base(error, ValidationCode.TooMuchMemory) { }

    internal MemoryLimitExceededException(TamgaApiError error, Exception? errorBodyParseFailure) : base(error, ValidationCode.TooMuchMemory, errorBodyParseFailure) { }
}

/// <summary>
/// <c>422 DISK_LIMIT_EXCEEDED</c> — disk quota refused at creation time. Validate-time equivalent:
/// <see cref="ValidationCode.TooMuchDisk"/>. Counted in megabytes, same as
/// <see cref="MemoryLimitExceededException"/>.
/// </summary>
public sealed class DiskLimitExceededException : TamgaLimitExceededException
{
    /// <summary>Constructs from a parsed API error.</summary>
    public DiskLimitExceededException(TamgaApiError error) : base(error, ValidationCode.TooMuchDisk) { }

    internal DiskLimitExceededException(TamgaApiError error, Exception? errorBodyParseFailure) : base(error, ValidationCode.TooMuchDisk, errorBodyParseFailure) { }
}

/// <summary><c>422 TOO_MANY_PROCESSES</c> — <c>POST /processes</c> refused: the license is at its <c>max_processes</c> limit. Validate-time equivalent: <see cref="ValidationCode.TooManyProcesses"/>.</summary>
public sealed class TooManyProcessesException : TamgaLimitExceededException
{
    /// <summary>Constructs from a parsed API error.</summary>
    public TooManyProcessesException(TamgaApiError error) : base(error, ValidationCode.TooManyProcesses) { }

    internal TooManyProcessesException(TamgaApiError error, Exception? errorBodyParseFailure) : base(error, ValidationCode.TooManyProcesses, errorBodyParseFailure) { }
}

/// <summary>
/// Thrown by <see cref="TamgaClient.ActivateMachineAsync"/> when the create succeeded, license
/// validation then answered an over-limit code, and — because <c>deleteOnOverLimit</c> was
/// <see langword="true"/> — the machine was deleted again. Built client-side; no server error
/// occurred.
/// </summary>
/// <remarks>
/// <para>
/// Exists because handing back the deleted machine as a success value was a trap (audit D15): a
/// caller that did not read <c>validation.Valid</c> — or did, and carried on — held a
/// <see cref="Models.Machine"/> whose row no longer existed, and every heartbeat on it answered
/// <c>404</c>. The rollback is now a failure, and it carries everything the tuple did:
/// <see cref="Validation"/> is the verdict, <see cref="DeletedMachineId"/> is the row that is gone.
/// </para>
/// <para>
/// <see cref="TamgaApiException.Error"/> is synthesized the way
/// <see cref="SchemeNotSupportedException(string)"/> synthesizes its own. <c>Status</c> is
/// <c>422</c>, what the create-time refusal of the same overage answers. <c>Code</c> is the
/// validate-time wire value the server actually sent — one of <c>TOO_MANY_MACHINES</c>,
/// <c>TOO_MANY_CORES</c>, <c>TOO_MUCH_MEMORY</c>, <c>TOO_MUCH_DISK</c>, <c>TOO_MANY_PROCESSES</c> —
/// never a <c>*_LIMIT_EXCEEDED</c> code no server emitted, so
/// <see cref="TamgaErrorMapper"/> does not and must not produce this type.
/// <see cref="TamgaLimitExceededException.EquivalentValidationCode"/> equals
/// <see cref="Validation"/>'s <see cref="ValidationResult.Code"/>, so a single
/// <c>catch (TamgaLimitExceededException)</c> handles the create-time and the validate-time
/// overage with one value.
/// </para>
/// <para>
/// Only <see cref="TamgaClient.ActivateMachineAsync"/> throws it.
/// <see cref="TamgaClient.ActivateMachineIdempotentAsync"/> reports the same outcome as
/// <see cref="Models.MachineActivation.RolledBack"/> instead, because its result type has room to
/// say so.
/// </para>
/// </remarks>
public sealed class MachineOverLimitException : TamgaLimitExceededException
{
    /// <summary>Constructs from the validation verdict that triggered the rollback and the id of the machine that was deleted.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="validation"/> is <see langword="null"/>.</exception>
    public MachineOverLimitException(ValidationResult validation, Guid deletedMachineId)
        : base(BuildError(validation, deletedMachineId), validation.Code)
    {
        Validation = validation;
        DeletedMachineId = deletedMachineId;
    }

    /// <summary>The license validation that reported the overage — the same value the tuple overload used to return.</summary>
    public ValidationResult Validation { get; }

    /// <summary>The id of the machine this activation created and then deleted. Its row no longer exists; do not heartbeat, check out or cache it.</summary>
    public Guid DeletedMachineId { get; }

    private static TamgaApiError BuildError(ValidationResult validation, Guid deletedMachineId)
    {
        ArgumentNullException.ThrowIfNull(validation);
        var code = ValidationCodeConverter.ToWireString(validation.Code);
        return new TamgaApiError
        {
            Status = 422,
            Code = code,
            Detail = $"Machine {deletedMachineId} was created, license validation answered {code} ({validation.Detail}), and the machine was deleted again.",
        };
    }
}

/// <summary>
/// <c>401 LICENSE_SUSPENDED</c> — license-key authentication refused because the license is
/// suspended. Raised at the front door, before any per-endpoint check, so every call on that
/// credential fails this way.
/// </summary>
/// <remarks>Not retryable: it clears only when the license is reinstated, which this SDK's credential cannot do.</remarks>
public sealed class LicenseSuspendedException : TamgaLicenseAuthException
{
    /// <summary>Constructs from a parsed API error.</summary>
    public LicenseSuspendedException(TamgaApiError error) : base(error) { }

    internal LicenseSuspendedException(TamgaApiError error, Exception? errorBodyParseFailure) : base(error, errorBodyParseFailure) { }
}

/// <summary>
/// <c>401 LICENSE_EXPIRED</c> — license-key authentication refused because the license has expired
/// <em>and</em> its policy's <c>expiration_strategy</c> is <c>REVOKE_ACCESS</c> (or an unrecognized
/// value, which fails closed).
/// </summary>
/// <remarks>
/// Under <c>MAINTAIN_ACCESS</c>, <c>ALLOW_ACCESS</c> and <c>RESTRICT_ACCESS</c> an expired license
/// still authenticates — validation answers <see cref="ValidationCode.Expired"/> instead. So this
/// exception says something narrower than "the license expired": it says the policy chose to
/// revoke the credential outright.
/// </remarks>
public sealed class LicenseExpiredException : TamgaLicenseAuthException
{
    /// <summary>Constructs from a parsed API error.</summary>
    public LicenseExpiredException(TamgaApiError error) : base(error) { }

    internal LicenseExpiredException(TamgaApiError error, Exception? errorBodyParseFailure) : base(error, errorBodyParseFailure) { }
}

/// <summary>
/// <c>401 LICENSE_NOT_ALLOWED</c> — license-key authentication is not permitted for this license's
/// policy.
/// </summary>
/// <remarks>
/// This is a configuration precondition, not a transient auth failure: the server accepts a
/// license key only when the policy's <c>authentication_strategy</c> is <c>LICENSE</c> or
/// <c>MIXED</c>, and that column defaults to <c>'TOKEN'</c>. A freshly created policy therefore
/// rejects <see cref="AuthTransport.License"/>/<see cref="AuthTransport.BasicLicense"/> out of the
/// box. Retrying cannot help; the policy has to be changed.
/// </remarks>
public sealed class LicenseNotAllowedException : TamgaLicenseAuthException
{
    /// <summary>Constructs from a parsed API error.</summary>
    public LicenseNotAllowedException(TamgaApiError error) : base(error) { }

    internal LicenseNotAllowedException(TamgaApiError error, Exception? errorBodyParseFailure) : base(error, errorBodyParseFailure) { }
}

/// <summary>
/// Base type for the <c>401</c> refusals raised by license-key authentication itself, so a caller
/// can catch every "this credential cannot be used" case in one clause without enumerating the
/// specific codes.
/// </summary>
public abstract class TamgaLicenseAuthException : TamgaApiException
{
    /// <summary>Constructs from a parsed API error.</summary>
    protected TamgaLicenseAuthException(TamgaApiError error) : base(error) { }

    internal TamgaLicenseAuthException(TamgaApiError error, Exception? errorBodyParseFailure) : base(error, errorBodyParseFailure) { }
}

/// <summary>
/// Maps a parsed <see cref="TamgaApiError"/> to its typed exception. Dispatch is on
/// <see cref="TamgaApiError.Code"/> only.
/// </summary>
/// <remarks>
/// <para>
/// <c>429 TOO_MANY_REQUESTS</c> is deliberately absent from the table below. It is a real,
/// returned status, but it is absorbed one layer down: <see cref="TamgaTransport"/> retries a
/// rate-limited request with capped <c>Retry-After</c>/jittered backoff, so by the time an error
/// reaches this mapper the retry budget (<see cref="TamgaClientOptions.MaxRetries"/>) is already
/// spent and a distinct exception type would only tell the caller something it can no longer act
/// on. It surfaces as the catch-all <see cref="TamgaApiException"/> with
/// <see cref="TamgaApiError.Status"/> <c>429</c> intact.
/// </para>
/// <para>
/// <see cref="MachineOverLimitException"/> is likewise absent, for the opposite reason: it is
/// built client-side by <see cref="TamgaClient.ActivateMachineAsync"/> from a validate-time
/// <c>meta.code</c>, which is not an error-envelope code.
/// </para>
/// </remarks>
public static class TamgaErrorMapper
{
    /// <summary>Maps a parsed API error to the most specific typed exception matching its <see cref="TamgaApiError.Code"/>.</summary>
    /// <param name="error">The parsed API error to map.</param>
    /// <returns>The typed exception for <paramref name="error"/>'s <c>code</c>, or a base <see cref="TamgaApiException"/> if the code is unmodeled.</returns>
    public static TamgaApiException ToException(TamgaApiError error) => ToException(error, null);

    /// <summary>
    /// Maps a parsed API error to its typed exception, additionally chaining the failure that
    /// stopped the server's error envelope from binding.
    /// </summary>
    /// <param name="error">The parsed (or raw-body-recovered) API error to map.</param>
    /// <param name="errorBodyParseFailure">
    /// The envelope-binding failure, or <see langword="null"/> on the normal path. It is surfaced
    /// BOTH on <see cref="TamgaApiException.ErrorBodyParseFailure"/> and as the returned
    /// exception's <see cref="Exception.InnerException"/>, so SDK-aware code and generic logging
    /// tooling can each find it.
    /// </param>
    /// <returns>The typed exception for <paramref name="error"/>'s <c>code</c>, or a base <see cref="TamgaApiException"/> if the code is unmodeled.</returns>
    public static TamgaApiException ToException(TamgaApiError error, Exception? errorBodyParseFailure) => error.Code switch
    {
        "CHECK_IN_NOT_REQUIRED" => new CheckInNotRequiredException(error, errorBodyParseFailure),
        "FINGERPRINT_TAKEN" => new FingerprintTakenException(error, errorBodyParseFailure),
        "PID_TAKEN" => new PidTakenException(error, errorBodyParseFailure),
        "KEY_TAKEN" => new KeyTakenException(error, errorBodyParseFailure),
        "TTL_INVALID" => new TtlInvalidException(error, errorBodyParseFailure),
        "LICENSE_NOT_ENCRYPTED" => new LicenseNotEncryptedException(error, errorBodyParseFailure),
        "LICENSE_KEY_MISSING" => new LicenseKeyMissingException(error, errorBodyParseFailure),
        "SCHEME_NOT_SUPPORTED" => new SchemeNotSupportedException(error, errorBodyParseFailure),
        "SIGNING_KEY_MISSING" => new SigningKeyMissingException(error, errorBodyParseFailure),
        "SECRET_KEY_MISSING" => new SecretKeyMissingException(error, errorBodyParseFailure),
        "DATASET_INVALID" => new DatasetInvalidException(error, errorBodyParseFailure),
        "MACHINE_LIMIT_EXCEEDED" => new MachineLimitExceededException(error, errorBodyParseFailure),
        "CORE_LIMIT_EXCEEDED" => new CoreLimitExceededException(error, errorBodyParseFailure),
        "MEMORY_LIMIT_EXCEEDED" => new MemoryLimitExceededException(error, errorBodyParseFailure),
        "DISK_LIMIT_EXCEEDED" => new DiskLimitExceededException(error, errorBodyParseFailure),
        "TOO_MANY_PROCESSES" => new TooManyProcessesException(error, errorBodyParseFailure),
        "LICENSE_SUSPENDED" => new LicenseSuspendedException(error, errorBodyParseFailure),
        "LICENSE_EXPIRED" => new LicenseExpiredException(error, errorBodyParseFailure),
        "LICENSE_NOT_ALLOWED" => new LicenseNotAllowedException(error, errorBodyParseFailure),
        "NOT_FOUND" => new TamgaNotFoundException(error, errorBodyParseFailure),
        "UNAUTHORIZED" => new TamgaUnauthorizedException(error, errorBodyParseFailure),
        "FORBIDDEN" => new TamgaForbiddenException(error, errorBodyParseFailure),
        "INTERNAL_SERVER_ERROR" => new TamgaInternalServerErrorException(error, errorBodyParseFailure),
        _ => new TamgaApiException(error, errorBodyParseFailure),
    };
}
