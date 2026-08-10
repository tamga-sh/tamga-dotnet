using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tamga.Sdk.Crypto;
using Tamga.Sdk.Models;

namespace Tamga.Sdk.Checkout;

/// <summary>
/// The inner <c>{enc, sig, alg}</c> JSON structure carried inside a <c>.machine</c> file's PEM
/// envelope — same shape as <see cref="LicenseFileCertificate"/>.
/// </summary>
public sealed record MachineFileCertificate
{
    /// <summary>The payload: base64-encoded AES-256-GCM ciphertext (encrypted files) or plain base64-encoded JSON (unencrypted files).</summary>
    [JsonPropertyName("enc")]
    public required string Enc { get; init; }

    /// <summary>The signature over <see cref="Enc"/>'s base64 string bytes, base64-encoded.</summary>
    [JsonPropertyName("sig")]
    public required string Sig { get; init; }

    /// <summary>
    /// The algorithm identifier reported by the server (e.g. contains <c>"aes-256-gcm"</c> and/or a
    /// signature-scheme suffix like <c>"rsa-sha256"</c>). NEVER used to select the verifier — see
    /// <see cref="MachineFile"/>'s type-level remarks.
    /// </summary>
    [JsonPropertyName("alg")]
    public required string Alg { get; init; }
}

/// <summary>The <c>{"data": &lt;MachineResource&gt;}</c> payload embedded in a plain (unencrypted) <c>.machine</c> file.</summary>
public sealed record MachineFilePayload
{
    /// <summary>The machine resource embedded in the file's payload.</summary>
    [JsonPropertyName("data")]
    public required JsonApiResource<MachineAttributes> Data { get; init; }
}

/// <summary>
/// Parses, verifies, and decrypts an offline <c>.machine</c> file:
/// <code>
/// -----BEGIN MACHINE FILE-----
/// &lt;base64 of JSON: { "enc": "&lt;base64&gt;", "sig": "&lt;base64 sig&gt;", "alg": "..." }&gt;
/// -----END MACHINE FILE-----
/// </code>
/// Same inner <c>{enc, sig, alg}</c> JSON shape as <see cref="LicenseFile"/>.
/// </summary>
/// <remarks>
/// GOTCHA: signing scheme is taken from the LICENSE's <c>scheme</c> field
/// (<see cref="LicenseScheme"/>), NOT hardcoded Ed25519 like license checkout (§E). This type's
/// verify dispatch selects Ed25519 / RSA-PKCS1 / RSA-PSS / ECDSA-P256 based on a caller-supplied
/// <see cref="LicenseScheme"/> parameter — NEVER by parsing this file's own <c>alg</c> string,
/// since <c>RSA_2048_PKCS1_SIGN</c> and <c>RSA_2048_JWT_RS256</c> both serialize to the same
/// <c>"rsa-sha256"</c> <c>alg</c> suffix server-side (an algorithm-confusion risk if dispatch were
/// keyed on the self-declared string instead of the caller's own trusted scheme value). An unset
/// license scheme (<see cref="LicenseScheme.None"/>) defaults to Ed25519, matching server behavior.
///
/// <c>RSA_2048_JWT_RS256</c> is explicitly rejected server-side for machine files
/// (<c>422 SCHEME_NOT_SUPPORTED</c>) — this type's verifier does NOT implement or attempt
/// JWT/RS256 verification for machine files; it throws <see cref="SchemeNotSupportedException"/>
/// immediately rather than silently no-op-ing.
///
/// Encryption key derivation is HKDF-SHA256 (<see cref="Hkdf"/>) — NOT the naive zero-pad/truncate
/// scheme used by license checkout (<see cref="NaiveKey"/>). Decryption requires BOTH the license
/// key AND the target machine's fingerprint. GOTCHA: <c>ttl</c> is server-validated
/// <c>&gt; 0 &amp;&amp; &lt;= 31536000</c> (365 days) — the SDK's checkout call validates this
/// client-side too, to fail fast, in addition to handling the server's <c>422 TTL_INVALID</c>.
///
/// RSA and ECDSA public keys are expected in X.509 <c>SubjectPublicKeyInfo</c> DER encoding (as
/// produced by <c>RSA.ExportSubjectPublicKeyInfo()</c>/<c>ECDsa.ExportSubjectPublicKeyInfo()</c>);
/// Ed25519 public keys are raw 32-byte keys, matching <see cref="LicenseFile"/>.
/// </remarks>
public sealed class MachineFile
{
    private const string BeginMarker = "-----BEGIN MACHINE FILE-----";
    private const string EndMarker = "-----END MACHINE FILE-----";

    /// <summary>The maximum <c>ttl</c> the server accepts for machine checkout: 365 days in seconds.</summary>
    public const int MaxTtlSeconds = 31536000;

    /// <summary>The parsed, unverified <c>{enc, sig, alg}</c> certificate.</summary>
    public MachineFileCertificate Certificate { get; }

    private MachineFile(MachineFileCertificate certificate)
    {
        Certificate = certificate;
    }

    /// <summary>Parses a PEM-wrapped <c>.machine</c> file. Does NOT verify the signature.</summary>
    /// <exception cref="OfflineFileFormatException">The PEM envelope or inner JSON is malformed.</exception>
    public static MachineFile Parse(string pem)
    {
        var inner = PemEnvelope.Strip(pem, BeginMarker, EndMarker);
        byte[] jsonBytes;
        try
        {
            jsonBytes = Convert.FromBase64String(inner);
        }
        catch (FormatException ex)
        {
            throw new OfflineFileFormatException($"Machine file body is not valid base64: {ex.Message}");
        }

        MachineFileCertificate? cert;
        try
        {
            cert = JsonSerializer.Deserialize<MachineFileCertificate>(jsonBytes, TamgaJsonOptions.Default);
        }
        catch (JsonException ex)
        {
            throw new OfflineFileFormatException($"Machine file certificate JSON is malformed: {ex.Message}");
        }

        return new MachineFile(cert ?? throw new OfflineFileFormatException("Machine file certificate JSON was null."));
    }

    /// <summary>Client-side validation mirroring the server's <c>422 TTL_INVALID</c> check — fails fast before a checkout request is even sent.</summary>
    public static void ValidateTtl(int ttl)
    {
        if (ttl <= 0 || ttl > MaxTtlSeconds)
        {
            throw new TtlInvalidException(new TamgaApiError
            {
                Status = 422,
                Code = "TTL_INVALID",
                Detail = $"ttl must be > 0 and <= {MaxTtlSeconds} (365 days); got {ttl}.",
            });
        }
    }

    /// <summary>
    /// Verifies the signature against the account's public key, dispatching by the caller-supplied
    /// <paramref name="scheme"/> — NEVER by parsing this file's own <c>alg</c> string. See
    /// type-level remarks for the algorithm-confusion rationale.
    /// </summary>
    /// <exception cref="SchemeNotSupportedException"><paramref name="scheme"/> is <see cref="LicenseScheme.Rsa2048JwtRs256"/> — never implemented for machine files.</exception>
    public bool Verify(LicenseScheme scheme, ReadOnlySpan<byte> publicKey)
    {
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(Certificate.Sig);
        }
        catch (FormatException)
        {
            return false;
        }

        var message = Encoding.UTF8.GetBytes(Certificate.Enc);

        return scheme switch
        {
            LicenseScheme.Rsa2048JwtRs256 => throw new SchemeNotSupportedException(
                "RSA_2048_JWT_RS256 is rejected server-side for machine files (422 SCHEME_NOT_SUPPORTED) and is not implemented client-side either — this SDK never attempts JWT/RS256 verification."),
            LicenseScheme.None or LicenseScheme.Ed25519Sign => Ed25519.Verify(publicKey, message, signature),
            LicenseScheme.Rsa2048Pkcs1Sign => VerifyRsa(publicKey, message, signature, RSASignaturePadding.Pkcs1),
            LicenseScheme.Rsa2048Pkcs1PssSign => VerifyRsa(publicKey, message, signature, RSASignaturePadding.Pss),
            LicenseScheme.EcdsaP256Sign => VerifyEcdsa(publicKey, message, signature),
            _ => throw new UnsupportedAlgorithmException($"Unsupported license scheme for machine file verification: {scheme}."),
        };
    }

    private static bool VerifyRsa(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature, RSASignaturePadding padding)
    {
        using var rsa = RSA.Create();
        try
        {
            rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
        }
        catch (CryptographicException)
        {
            return false;
        }

        return rsa.VerifyData(message, signature, HashAlgorithmName.SHA256, padding);
    }

    private static bool VerifyEcdsa(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        using var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);
        }
        catch (CryptographicException)
        {
            return false;
        }

        return Ecdsa.Verify(ecdsa, message, signature);
    }

    /// <summary>
    /// Full verify pipeline: verifies the signature (fails closed), then decrypts (if <c>alg</c>
    /// indicates AES-256-GCM, using the HKDF-derived key — see <see cref="Hkdf"/>) or
    /// plain-decodes the <c>enc</c> payload, and parses the embedded
    /// <c>{"data": &lt;MachineResource&gt;}</c> JSON.
    /// </summary>
    /// <param name="scheme">The license's signing scheme — drives verifier dispatch, see type-level remarks.</param>
    /// <param name="publicKey">The account's public key, in the format documented on the type.</param>
    /// <param name="licenseKey">The license key — HKDF input keying material for an encrypted file.</param>
    /// <param name="fingerprint">The target machine's fingerprint — HKDF <c>info</c> for an encrypted file. Decryption fails closed (AES-GCM auth failure) if this doesn't match the machine the file was issued for.</param>
    public Machine VerifyAndDecrypt(LicenseScheme scheme, ReadOnlySpan<byte> publicKey, string licenseKey, string fingerprint)
    {
        if (!Verify(scheme, publicKey))
        {
            throw new SignatureVerificationException("Machine file signature verification failed — the file may be forged or corrupted.");
        }

        byte[] payloadBytes;
        try
        {
            payloadBytes = Convert.FromBase64String(Certificate.Enc);
        }
        catch (FormatException ex)
        {
            throw new OfflineFileFormatException($"Machine file 'enc' is not valid base64: {ex.Message}");
        }

        byte[] jsonBytes;
        if (Certificate.Alg.Contains("aes-256-gcm", StringComparison.Ordinal))
        {
            jsonBytes = DecryptPayload(payloadBytes, licenseKey, fingerprint);
        }
        else if (Certificate.Alg.Contains("base64", StringComparison.Ordinal))
        {
            jsonBytes = payloadBytes;
        }
        else
        {
            throw new UnsupportedAlgorithmException($"Unsupported machine file algorithm: '{Certificate.Alg}'.");
        }

        MachineFilePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<MachineFilePayload>(jsonBytes, TamgaJsonOptions.Default);
        }
        catch (JsonException ex)
        {
            throw new OfflineFileFormatException($"Machine file payload JSON is malformed: {ex.Message}");
        }

        if (payload is null)
        {
            throw new OfflineFileFormatException("Machine file payload was empty.");
        }

        return Machine.FromResource(payload.Data);
    }

    private static byte[] DecryptPayload(byte[] payloadBytes, string licenseKey, string fingerprint)
    {
        var minLength = AesGcmCipher.NonceLength + AesGcmCipher.TagLength;
        if (payloadBytes.Length < minLength)
        {
            throw new OfflineFileFormatException($"Encrypted machine file payload too short: expected at least {minLength} bytes, got {payloadBytes.Length}.");
        }

        var nonce = payloadBytes.AsSpan(0, AesGcmCipher.NonceLength);
        var tag = payloadBytes.AsSpan(payloadBytes.Length - AesGcmCipher.TagLength, AesGcmCipher.TagLength);
        var ciphertext = payloadBytes.AsSpan(AesGcmCipher.NonceLength, payloadBytes.Length - AesGcmCipher.NonceLength - AesGcmCipher.TagLength);

        var key = Hkdf.DeriveMachineFileKey(licenseKey, fingerprint);

        try
        {
            return AesGcmCipher.Open(key, nonce, ciphertext, tag);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new SignatureVerificationException($"Machine file decryption failed authentication (wrong license key/fingerprint, or tampered payload): {ex.Message}");
        }
        catch (CryptographicException ex)
        {
            throw new SignatureVerificationException($"Machine file decryption failed: {ex.Message}");
        }
    }
}
