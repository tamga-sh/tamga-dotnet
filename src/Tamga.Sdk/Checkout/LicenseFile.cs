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
    [JsonPropertyName("enc")]
    public required string Enc { get; init; }

    [JsonPropertyName("sig")]
    public required string Sig { get; init; }

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
/// <c>alg</c> is exactly <c>"base64+ed25519"</c> (plain) or <c>"aes-256-gcm+ed25519"</c>
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
/// GOTCHA: <c>includes</c> is always <c>[]</c> server-side — this SDK does not model an
/// "embedded relationships via checkout" feature. GOTCHA: checkout <c>id</c> is a fresh UUIDv7 per
/// call, not idempotent. GOTCHA: <c>ttl</c>/<c>expiry</c> (returned alongside the certificate by
/// the JSON:API checkout response, not carried inside the file itself) are metadata-only, NOT
/// embedded in the signed payload and NOT re-checked server-side on later validation — expiry
/// enforcement for an offline file is entirely this SDK's client-side responsibility. GOTCHA: the
/// server returns <c>422 LICENSE_NOT_ENCRYPTED</c> when the license has no <c>key</c> set and
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

    /// <summary>Parses a PEM-wrapped <c>.lic</c> file. Does NOT verify the signature — call <see cref="Verify"/> or <see cref="VerifyAndDecrypt"/> separately.</summary>
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
    /// fail-closed exception should use <see cref="VerifyAndDecrypt"/>.
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
    /// Full verify pipeline: verifies the Ed25519 signature (fails closed), then decrypts (if
    /// <c>alg</c> indicates AES-256-GCM) or plain-decodes the <c>enc</c> payload, and parses the
    /// embedded <c>{"data": &lt;LicenseResource&gt;}</c> JSON into a <see cref="License"/>.
    /// </summary>
    /// <param name="publicKey">The account's raw 32-byte Ed25519 public key.</param>
    /// <param name="licenseKey">
    /// The license key, used to derive the AES-256-GCM key (via <see cref="NaiveKey"/>) for an
    /// encrypted file. Ignored for a plain (unencrypted) file, but still required by this method's
    /// signature for a uniform call shape across both cases.
    /// </param>
    /// <exception cref="SignatureVerificationException">Signature verification failed — the file may be forged or corrupted.</exception>
    /// <exception cref="UnsupportedAlgorithmException"><see cref="LicenseFileCertificate.Alg"/> is not a recognized value.</exception>
    /// <exception cref="OfflineFileFormatException">The decrypted/decoded payload is not valid JSON in the expected shape.</exception>
    public License VerifyAndDecrypt(ReadOnlySpan<byte> publicKey, string licenseKey)
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

        return License.FromResource(payload.Data);
    }

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

        // CRITICAL: not a KDF — see NaiveKey.cs. Zero-pad/truncate transform of the raw license
        // key string, exactly as the server derives its own AES key for this format.
        var key = NaiveKey.Derive(licenseKey);

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
