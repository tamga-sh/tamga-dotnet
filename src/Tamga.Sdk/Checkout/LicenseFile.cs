using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tamga.Sdk.Crypto;
using Tamga.Sdk.Models;

namespace Tamga.Sdk.Checkout;

/// <summary>
/// The inner <c>{enc, sig, alg}</c> JSON structure carried inside a <c>.lic</c> file's PEM
/// envelope. Field order (<c>enc, sig, alg</c>) is guaranteed on serialize since this is a real
/// C# type, not a dictionary.
/// </summary>
public sealed record LicenseFileCertificate
{
    /// <summary>
    /// Base64-encoded license payload — either AES-256-GCM ciphertext (encrypted license) or plain
    /// JSON (unencrypted), depending on <see cref="Alg"/>.
    /// </summary>
    [JsonPropertyName("enc")]
    public required string Enc { get; init; }

    /// <summary>
    /// Base64-encoded Ed25519 signature, computed over the ASCII/UTF-8 bytes of <see cref="Enc"/>'s
    /// base64 string itself, not the decoded payload bytes.
    /// </summary>
    [JsonPropertyName("sig")]
    public required string Sig { get; init; }

    /// <summary>Algorithm identifier — exactly <c>"base64+ed25519+v2"</c> (plain) or <c>"aes-256-gcm+ed25519+v2"</c> (encrypted).</summary>
    [JsonPropertyName("alg")]
    public required string Alg { get; init; }
}

/// <summary>
/// Parses, verifies, and decrypts an offline <c>.lic</c> license file:
/// <code>
/// -----BEGIN LICENSE FILE-----
/// &lt;base64 of JSON: { "enc": "&lt;base64&gt;", "sig": "&lt;base64 ed25519 sig&gt;", "alg": "..." }&gt;
/// -----END LICENSE FILE-----
/// </code>
/// </summary>
/// <remarks>
/// <c>alg</c> is exactly <c>"base64+ed25519+v2"</c> (plain) or <c>"aes-256-gcm+ed25519+v2"</c>
/// (encrypted) — Ed25519 ONLY for the checkout signature, independent of the license's own
/// <see cref="LicenseScheme"/> (contrast with <see cref="MachineFile"/>, which dispatches by
/// scheme).
///
/// CRITICAL — the single most consequential correctness trap in this SDK: the Ed25519 signature
/// covers <c>enc</c>'s ASCII/UTF-8 bytes of the BASE64 STRING ITSELF, NOT the base64-decoded
/// bytes. Get the byte source wrong and every <c>.lic</c> file either fails verification (safe but
/// broken) or, worse, a bug that skips verification silently accepts forged files. See the
/// <c>// CRITICAL:</c> comment at the call site in <see cref="Verify"/>.
///
/// Format v2 is mandatory: <c>alg</c> must end in <c>+v2</c> and the payload must carry the signed
/// <c>meta</c> claims (<c>iat</c>/<c>exp</c>/<c>jti</c>/<c>kid</c>, see
/// <see cref="LicenseFileClaims"/>). A pre-v2 file is rejected outright with no fallback path —
/// see <see cref="VerifyWithClaims"/>.
///
/// GOTCHA: <c>includes</c> is always <c>[]</c> server-side — this SDK does not model an
/// "embedded relationships via checkout" feature. GOTCHA: checkout <c>id</c> is a fresh UUIDv7 per
/// call, not idempotent. GOTCHA: the <c>ttl</c>/<c>expiry</c> fields returned alongside the
/// certificate in the JSON:API checkout response are envelope metadata only — they are not signed
/// and not re-checked server-side on later validation. The expiry that actually binds is the
/// <c>exp</c> claim inside the signed payload, which this type enforces on every verify. GOTCHA:
/// the server returns <c>422 LICENSE_NOT_ENCRYPTED</c> when the license has no <c>key</c> set and
/// <c>encrypt=true</c> is requested.
/// </remarks>
public sealed class LicenseFile
{
    private const string BeginMarker = "-----BEGIN LICENSE FILE-----";
    private const string EndMarker = "-----END LICENSE FILE-----";

    /// <summary>The parsed, unverified <c>{enc, sig, alg}</c> certificate.</summary>
    public LicenseFileCertificate Certificate { get; }

    private LicenseFile(LicenseFileCertificate certificate)
    {
        Certificate = certificate;
    }

    /// <summary>Parses a PEM-wrapped <c>.lic</c> file. Does NOT verify the signature — call <see cref="Verify"/> or <see cref="VerifyAndDecrypt(System.ReadOnlySpan{byte}, string)"/> separately.</summary>
    /// <exception cref="OfflineFileFormatException">The PEM envelope or inner JSON is malformed.</exception>
    public static LicenseFile Parse(string pem)
    {
        var inner = PemEnvelope.Strip(pem, BeginMarker, EndMarker);
        byte[] jsonBytes;
        try
        {
            jsonBytes = Convert.FromBase64String(inner);
        }
        catch (FormatException ex)
        {
            throw new OfflineFileFormatException($"License file body is not valid base64: {ex.Message}");
        }

        LicenseFileCertificate? cert;
        try
        {
            cert = JsonSerializer.Deserialize<LicenseFileCertificate>(jsonBytes, TamgaJsonOptions.Default);
        }
        catch (JsonException ex)
        {
            throw new OfflineFileFormatException($"License file certificate JSON is malformed: {ex.Message}");
        }

        return new LicenseFile(cert ?? throw new OfflineFileFormatException("License file certificate JSON was null."));
    }

    /// <summary>
    /// Verifies the Ed25519 signature against the account's public key. Returns
    /// <see langword="true"/>/<see langword="false"/> rather than throwing — callers that need a
    /// fail-closed exception should use <see cref="VerifyAndDecrypt(System.ReadOnlySpan{byte}, string)"/>.
    /// </summary>
    /// <exception cref="UnsupportedAlgorithmException"><see cref="LicenseFileCertificate.Alg"/> does not contain <c>"ed25519"</c>.</exception>
    public bool Verify(ReadOnlySpan<byte> publicKey)
    {
        if (!Certificate.Alg.Contains("ed25519", StringComparison.Ordinal))
        {
            throw new UnsupportedAlgorithmException($"Unsupported license file algorithm: '{Certificate.Alg}'. Only ed25519-signed license files are supported.");
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(Certificate.Sig);
        }
        catch (FormatException)
        {
            return false;
        }

        // CRITICAL: sign/verify over `enc`'s base64 STRING bytes (UTF-8 of the string itself), NOT
        // the base64-decoded payload bytes. This is the single most consequential correctness trap
        // in this SDK — see type-level remarks above.
        var message = Encoding.UTF8.GetBytes(Certificate.Enc);
        return Ed25519.Verify(publicKey, message, signature);
    }

    /// <summary>
    /// Full verify pipeline: verifies the Ed25519 signature (fails closed), rejects anything that
    /// is not format v2, decrypts (if <c>alg</c> indicates AES-256-GCM) or plain-decodes the
    /// <c>enc</c> payload, enforces the signed <c>exp</c> claim, and parses the embedded
    /// <c>{"data": &lt;LicenseResource&gt;, "meta": &lt;claims&gt;}</c> JSON into a
    /// <see cref="License"/>. Uses the local clock; the overload taking
    /// <c>nowUnixSeconds</c> lets a caller supply a trusted timestamp instead.
    /// </summary>
    /// <param name="publicKey">The account's raw 32-byte Ed25519 public key.</param>
    /// <param name="licenseKey">
    /// The license key, used to derive the AES-256-GCM key (via
    /// <see cref="Hkdf.DeriveLicenseFileKey"/>) for an encrypted file. Ignored for a plain
    /// (unencrypted) file, but still required by this method's signature for a uniform call shape
    /// across both cases.
    /// </param>
    /// <exception cref="SignatureVerificationException">Signature verification failed — the file may be forged or corrupted — or decryption failed its authentication tag.</exception>
    /// <exception cref="UnsupportedAlgorithmException"><see cref="LicenseFileCertificate.Alg"/> is not a recognized value, or does not end in <c>+v2</c> (a pre-v2 file).</exception>
    /// <exception cref="LicenseFileExpiredException">The signature verified but the signed <c>exp</c> claim has passed, allowing 60 seconds of clock skew.</exception>
    /// <exception cref="OfflineFileFormatException">The decrypted/decoded payload is not valid JSON in the expected shape, or carries no signed <c>meta</c> claims.</exception>
    public License VerifyAndDecrypt(ReadOnlySpan<byte> publicKey, string licenseKey)
        => VerifyAndDecrypt(publicKey, licenseKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    /// <summary>
    /// As <see cref="VerifyAndDecrypt(ReadOnlySpan{byte}, string)"/>, with the current time
    /// supplied by the caller.
    /// </summary>
    /// <remarks>
    /// Two uses. Tests get determinism. And an application that keeps a server-supplied timestamp —
    /// the recommended defence against a user winding the system clock back to revive an expired
    /// file — can pass that instead of trusting the local clock.
    /// </remarks>
    public License VerifyAndDecrypt(ReadOnlySpan<byte> publicKey, string licenseKey, long nowUnixSeconds)
        => VerifyWithClaims(publicKey, licenseKey, nowUnixSeconds).License;

    /// <summary>
    /// As <see cref="VerifyAndDecrypt(ReadOnlySpan{byte}, string)"/>, also returning the signed
    /// claims. Use this for <c>jti</c> replay detection or <c>kid</c> key-rotation bookkeeping.
    /// Expiry is enforced either way — it is not opt-in.
    /// </summary>
    /// <param name="publicKey">The account's raw 32-byte Ed25519 public key.</param>
    /// <param name="licenseKey">The license key, used to derive the AES-256-GCM key for an encrypted file.</param>
    /// <param name="nowUnixSeconds">The current time, seconds since the Unix epoch, used for the <c>exp</c> check.</param>
    /// <returns>The license carried by the file, together with the claims that were inside the signed bytes.</returns>
    /// <exception cref="SignatureVerificationException">Signature verification or payload decryption failed.</exception>
    /// <exception cref="UnsupportedAlgorithmException"><see cref="LicenseFileCertificate.Alg"/> is unrecognized or pre-v2.</exception>
    /// <exception cref="LicenseFileExpiredException">The signed <c>exp</c> claim has passed, allowing 60 seconds of clock skew.</exception>
    /// <exception cref="OfflineFileFormatException">The payload is malformed or carries no signed <c>meta</c> claims.</exception>
    public (License License, LicenseFileClaims Claims) VerifyWithClaims(
        ReadOnlySpan<byte> publicKey,
        string licenseKey,
        long nowUnixSeconds)
    {
        if (!Verify(publicKey))
        {
            throw new SignatureVerificationException("License file signature verification failed — the file may be forged or corrupted.");
        }

        byte[] payloadBytes;
        try
        {
            payloadBytes = Convert.FromBase64String(Certificate.Enc);
        }
        catch (FormatException ex)
        {
            throw new OfflineFileFormatException($"License file 'enc' is not valid base64: {ex.Message}");
        }

        byte[] jsonBytes;
        // The +v2 suffix is load-bearing: a v1 file carried no expiry inside its signature, so
        // accepting one would hand back the permanent-file problem v2 exists to close.
        if (!Certificate.Alg.EndsWith("+v2", StringComparison.Ordinal))
        {
            throw new UnsupportedAlgorithmException($"Unsupported license file algorithm: '{Certificate.Alg}'.");
        }

        if (Certificate.Alg.Contains("aes-256-gcm", StringComparison.Ordinal))
        {
            jsonBytes = DecryptPayload(payloadBytes, licenseKey);
        }
        else if (Certificate.Alg.Contains("base64", StringComparison.Ordinal))
        {
            jsonBytes = payloadBytes;
        }
        else
        {
            throw new UnsupportedAlgorithmException($"Unsupported license file algorithm: '{Certificate.Alg}'.");
        }

        LicenseFilePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<LicenseFilePayload>(jsonBytes, TamgaJsonOptions.Default);
        }
        catch (JsonException ex)
        {
            throw new OfflineFileFormatException($"License file payload JSON is malformed: {ex.Message}");
        }

        if (payload is null)
        {
            throw new OfflineFileFormatException("License file payload was empty.");
        }

        // Second line behind the alg gate: a file must not reach the expiry check with nothing
        // to check.
        if (payload.Meta is null)
        {
            throw new OfflineFileFormatException(
                "License file payload is missing the signed 'meta' claims (this looks like a pre-v2 file).");
        }

        // The signature proves the file is authentic. It does not prove it is still valid — that
        // is this check, and skipping it is what made v1 files permanent.
        if (payload.Meta.ExpiresAt is { } exp && nowUnixSeconds - ClockSkewToleranceSeconds > exp)
        {
            throw new LicenseFileExpiredException(exp);
        }

        return (License.FromResource(payload.Data), payload.Meta);
    }

    /// <summary>
    /// How much clock skew is tolerated when checking <c>exp</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately small. The client's clock is under the attacker's control, so a generous
    /// allowance is just a free extension on every expired file; this covers ordinary NTP drift
    /// and nothing more.
    /// </remarks>
    private const long ClockSkewToleranceSeconds = 60;

    private static byte[] DecryptPayload(byte[] payloadBytes, string licenseKey)
    {
        var minLength = AesGcmCipher.NonceLength + AesGcmCipher.TagLength;
        if (payloadBytes.Length < minLength)
        {
            throw new OfflineFileFormatException($"Encrypted license file payload too short: expected at least {minLength} bytes, got {payloadBytes.Length}.");
        }

        var nonce = payloadBytes.AsSpan(0, AesGcmCipher.NonceLength);
        var tag = payloadBytes.AsSpan(payloadBytes.Length - AesGcmCipher.TagLength, AesGcmCipher.TagLength);
        var ciphertext = payloadBytes.AsSpan(AesGcmCipher.NonceLength, payloadBytes.Length - AesGcmCipher.NonceLength - AesGcmCipher.TagLength);

        var key = Hkdf.DeriveLicenseFileKey(licenseKey);

        try
        {
            return AesGcmCipher.Open(key, nonce, ciphertext, tag);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new SignatureVerificationException($"License file decryption failed authentication (wrong license key, or tampered payload): {ex.Message}");
        }
        catch (CryptographicException ex)
        {
            throw new SignatureVerificationException($"License file decryption failed: {ex.Message}");
        }
    }
}

/// <summary>Shared PEM-envelope stripping for <see cref="LicenseFile"/> and <see cref="MachineFile"/>.</summary>
internal static class PemEnvelope
{
    public static string Strip(string pem, string beginMarker, string endMarker)
    {
        var trimmed = pem.Trim();
        if (!trimmed.StartsWith(beginMarker, StringComparison.Ordinal))
        {
            throw new OfflineFileFormatException($"Missing '{beginMarker}' marker.");
        }

        if (!trimmed.EndsWith(endMarker, StringComparison.Ordinal))
        {
            throw new OfflineFileFormatException($"Missing '{endMarker}' marker.");
        }

        // SECURITY: StartsWith/EndsWith only guarantee the trimmed string is at least as long as
        // each marker individually — a short, attacker-crafted string can satisfy both
        // independently while being shorter than beginMarker.Length + endMarker.Length (the two
        // markers "overlap"). Without this guard the slice below computes a negative length and
        // throws an untyped ArgumentOutOfRangeException instead of the documented
        // OfflineFileFormatException, breaking callers that only catch the latter for untrusted
        // .lic/.machine input. Found in security review — see plan §E checkbox note.
        if (trimmed.Length < beginMarker.Length + endMarker.Length)
        {
            throw new OfflineFileFormatException($"Body between '{beginMarker}' and '{endMarker}' is malformed or too short.");
        }

        var body = trimmed[beginMarker.Length..^endMarker.Length];
        var builder = new StringBuilder(body.Length);
        foreach (var c in body)
        {
            if (!char.IsWhiteSpace(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
