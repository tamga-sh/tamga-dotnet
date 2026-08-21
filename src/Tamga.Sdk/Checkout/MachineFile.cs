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
    /// <summary>
    /// The payload. For a plain file (<c>alg</c> starting <c>base64</c>) this is one base64 blob
    /// of the payload JSON. For an encrypted file (<c>alg</c> starting <c>aes-256-gcm</c>) it is
    /// <c>"&lt;nonce_b64&gt;.&lt;ciphertext_b64&gt;"</c> — TWO separately base64-encoded halves
    /// joined by a literal <c>.</c>, where the second half already carries the 16-byte GCM tag.
    /// See <see cref="MachineFile"/>'s remarks; this differs from <see cref="LicenseFile"/>.
    /// </summary>
    [JsonPropertyName("enc")]
    public required string Enc { get; init; }

    /// <summary>The signature over <see cref="Enc"/>'s base64 string bytes, base64-encoded.</summary>
    [JsonPropertyName("sig")]
    public required string Sig { get; init; }

    /// <summary>
    /// The algorithm identifier reported by the server:
    /// <c>&lt;encoding&gt;+&lt;signing-suffix&gt;+v2</c>, e.g. <c>base64+ed25519+v2</c> or
    /// <c>aes-256-gcm+rsa-pss-sha256+v2</c>. NEVER used to select the verifier — see
    /// <see cref="MachineFile"/>'s type-level remarks.
    /// </summary>
    [JsonPropertyName("alg")]
    public required string Alg { get; init; }
}

/// <summary>
/// The <c>{"data": &lt;MachineResource&gt;, "meta": &lt;claims&gt;}</c> payload embedded in a
/// format-v2 <c>.machine</c> file.
/// </summary>
public sealed record MachineFilePayload
{
    /// <summary>The machine resource embedded in the file's payload.</summary>
    [JsonPropertyName("data")]
    public required JsonApiResource<MachineAttributes> Data { get; init; }

    /// <summary>
    /// The claims that were covered by the signature (<c>iat</c>/<c>exp</c>/<c>jti</c>/<c>kid</c>).
    /// Machine files carry the same <see cref="LicenseFileClaims"/> shape as <c>.lic</c> files —
    /// the server builds both from one type. Absent only on a pre-v2 file, which is rejected.
    /// </summary>
    [JsonPropertyName("meta")]
    public LicenseFileClaims? Meta { get; init; }
}

/// <summary>
/// Parses, verifies, and decrypts an offline <c>.machine</c> file:
/// <code>
/// -----BEGIN MACHINE FILE-----
/// &lt;base64 of JSON: { "enc": "&lt;base64&gt;", "sig": "&lt;base64 sig&gt;", "alg": "..." }&gt;
/// -----END MACHINE FILE-----
/// </code>
/// Same inner <c>{enc, sig, alg}</c> JSON shape as <see cref="LicenseFile"/>, but NOT the same
/// <c>enc</c> encoding and NOT a fixed signature scheme — see the remarks.
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
/// The <c>alg</c> suffix is a CROSS-CHECK only: a file that disagrees with the caller about how it
/// was signed is refused, but it can never widen what the caller asked for.
///
/// <c>RSA_2048_JWT_RS256</c> is explicitly rejected server-side for machine files
/// (<c>422 SCHEME_NOT_SUPPORTED</c>) — this type's verifier does NOT implement or attempt
/// JWT/RS256 verification for machine files; it throws <see cref="SchemeNotSupportedException"/>
/// up front, before any parsing, rather than silently no-op-ing.
///
/// FORMAT v2 IS MANDATORY. <c>alg</c> is <c>&lt;encoding&gt;+&lt;signing-suffix&gt;+v2</c>: the
/// encoding prefix runs to the FIRST <c>+</c>, the version marker follows the LAST <c>+</c>, and
/// the signing suffix is everything between. Both <c>aes-256-gcm</c> and <c>rsa-pss-sha256</c>
/// contain hyphens and the suffix itself can contain them, so nothing here may be recovered by
/// substring sniffing — a <c>Contains("base64")</c> test happily accepts <c>xbase64+ed25519+v3</c>.
/// A file with no <c>+v2</c> is rejected outright: a v1 file carried no <c>exp</c> inside its
/// signature and derived its AES key by zero-padding the license key instead of HKDF, so accepting
/// one silently reinstates both weaknesses. Note <c>alg</c> is NOT covered by the signature (the
/// server signs <c>enc</c>'s bytes only), so it is attacker-malleable — which is exactly why it is
/// gated rather than trusted.
///
/// ENCRYPTED PAYLOAD LAYOUT. An encrypted <c>enc</c> is
/// <c>"&lt;nonce_b64&gt;.&lt;ciphertext_b64&gt;"</c>: two separately base64-encoded halves, the
/// second already including the 16-byte GCM tag. It is NOT one blob with a nonce sliced off the
/// first 12 bytes — that is what <see cref="LicenseFile"/> uses, and the two formats are genuinely
/// different server-side (<c>FieldEncryption::encrypt</c> vs the license path's own single-blob
/// encoder). Order is fixed: verify the signature over the whole <c>enc</c> STRING first, then
/// split, then decode, then decrypt. Never decode attacker-controlled bytes before the signature
/// has been checked.
///
/// Encryption key derivation is HKDF-SHA256 (<see cref="Hkdf.DeriveMachineFileKey"/>). License
/// checkout uses HKDF too, but with a different salt and <c>info</c>
/// (<see cref="Hkdf.DeriveLicenseFileKey"/>), so the two keys never collide and are not
/// interchangeable. Decryption here requires BOTH the license key AND the target machine's
/// fingerprint. GOTCHA: <c>ttl</c> is server-validated
/// <c>&gt; 0 &amp;&amp; &lt;= 31536000</c> (365 days) — the SDK's checkout call validates this
/// client-side too, to fail fast, in addition to handling the server's <c>422 TTL_INVALID</c>.
///
/// PUBLIC KEY ENCODING is not uniform, because the server does not hand out one encoding:
/// Ed25519 is a raw 32-byte key; ECDSA P-256 is a raw 65-byte SEC1 uncompressed point
/// (<c>0x04 || X || Y</c>); RSA is accepted as either PKCS#1 <c>RSAPublicKey</c> DER (what
/// <c>license_signing::extract_public_key</c> produces) or X.509 <c>SubjectPublicKeyInfo</c> DER
/// (what the account resource's <c>public_key</c> attribute carries). For ECDSA the curve is
/// pinned to P-256 by this SDK rather than read out of the caller's bytes, and
/// <see cref="Ecdsa.Verify"/> re-checks it.
/// </remarks>
public sealed class MachineFile
{
    private const string BeginMarker = "-----BEGIN MACHINE FILE-----";
    private const string EndMarker = "-----END MACHINE FILE-----";

    /// <summary>The mandatory format-version marker after the last <c>+</c> of <c>alg</c>.</summary>
    private const string FormatVersionMarker = "v2";

    /// <summary>The <c>alg</c> encoding prefix for a plain (unencrypted) payload.</summary>
    private const string PlainEncodingPrefix = "base64";

    /// <summary>The <c>alg</c> encoding prefix for an AES-256-GCM payload.</summary>
    private const string EncryptedEncodingPrefix = "aes-256-gcm";

    /// <summary>The separator between the two base64 halves of an encrypted <c>enc</c>.</summary>
    private const char EncryptedPartSeparator = '.';

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
    /// <remarks>
    /// SIGNATURE ONLY. This answers "were these bytes signed by that key", which is a true
    /// statement whatever <c>alg</c> claims. It deliberately does NOT apply the format-v2 gate or
    /// the <c>alg</c> cross-check — those are policy, they live in
    /// <see cref="VerifyWithClaims"/>, and a caller who wants the whole contract enforced must go
    /// through <see cref="VerifyAndDecrypt(LicenseScheme, ReadOnlySpan{byte}, string, string)"/>
    /// rather than treating a <see langword="true"/> here as "this file is good".
    /// </remarks>
    /// <exception cref="SchemeNotSupportedException"><paramref name="scheme"/> is <see cref="LicenseScheme.Rsa2048JwtRs256"/> — never implemented for machine files.</exception>
    public bool Verify(LicenseScheme scheme, ReadOnlySpan<byte> publicKey)
    {
        RejectJwtRs256(scheme);

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
            LicenseScheme.None or LicenseScheme.Ed25519Sign => Ed25519.Verify(publicKey, message, signature),
            LicenseScheme.Rsa2048Pkcs1Sign => VerifyRsa(publicKey, message, signature, RSASignaturePadding.Pkcs1),
            LicenseScheme.Rsa2048Pkcs1PssSign => VerifyRsa(publicKey, message, signature, RSASignaturePadding.Pss),
            LicenseScheme.EcdsaP256Sign => VerifyEcdsa(publicKey, message, signature),
            _ => throw new UnsupportedAlgorithmException($"Unsupported license scheme for machine file verification: {scheme}."),
        };
    }

    private static bool VerifyRsa(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature, RSASignaturePadding padding)
    {
        using var rsa = Rsa.TryImportPublicKey(publicKey);
        return rsa is not null && rsa.VerifyData(message, signature, HashAlgorithmName.SHA256, padding);
    }

    private static bool VerifyEcdsa(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        using var ecdsa = Ecdsa.TryImportPublicKey(publicKey);
        return ecdsa is not null && Ecdsa.Verify(ecdsa, message, signature);
    }

    /// <summary>
    /// Full verify pipeline: verifies the signature (fails closed), rejects anything that is not
    /// format v2 or whose <c>alg</c> contradicts <paramref name="scheme"/>, decrypts (AES-256-GCM
    /// under the HKDF-derived key — see <see cref="Hkdf"/>) or plain-decodes the <c>enc</c>
    /// payload, enforces the signed <c>exp</c> claim, and parses the embedded
    /// <c>{"data": &lt;MachineResource&gt;, "meta": &lt;claims&gt;}</c> JSON. Uses the local
    /// clock; the overload taking <c>nowUnixSeconds</c> lets a caller supply a trusted timestamp
    /// instead.
    /// </summary>
    /// <param name="scheme">The license's signing scheme — drives verifier dispatch, see type-level remarks.</param>
    /// <param name="publicKey">The account's public key, in the format documented on the type.</param>
    /// <param name="licenseKey">The license key — HKDF input keying material for an encrypted file.</param>
    /// <param name="fingerprint">The target machine's fingerprint — HKDF <c>info</c> for an encrypted file. Decryption fails closed (AES-GCM auth failure) if this doesn't match the machine the file was issued for.</param>
    /// <exception cref="SchemeNotSupportedException"><paramref name="scheme"/> is <see cref="LicenseScheme.Rsa2048JwtRs256"/>.</exception>
    /// <exception cref="SignatureVerificationException">Signature verification failed, or decryption failed its authentication tag.</exception>
    /// <exception cref="UnsupportedAlgorithmException"><c>alg</c> is malformed, pre-v2, or names a signing suffix that contradicts <paramref name="scheme"/>.</exception>
    /// <exception cref="LicenseFileExpiredException">The signature verified but the signed <c>exp</c> claim has passed, allowing 60 seconds of clock skew.</exception>
    /// <exception cref="OfflineFileFormatException">The decrypted/decoded payload is not valid JSON in the expected shape, or carries no signed <c>meta</c> claims.</exception>
    public Machine VerifyAndDecrypt(LicenseScheme scheme, ReadOnlySpan<byte> publicKey, string licenseKey, string fingerprint)
        => VerifyAndDecrypt(scheme, publicKey, licenseKey, fingerprint, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    /// <summary>
    /// As <see cref="VerifyAndDecrypt(LicenseScheme, ReadOnlySpan{byte}, string, string)"/>, with
    /// the current time supplied by the caller.
    /// </summary>
    /// <remarks>
    /// Two uses, mirroring <see cref="LicenseFile"/>. Tests get determinism. And an application
    /// that keeps a server-supplied timestamp — the recommended defence against a user winding the
    /// system clock back to revive an expired file — can pass that instead of trusting the local
    /// clock, which on an offline-licensing client is under the attacker's control by definition.
    /// </remarks>
    public Machine VerifyAndDecrypt(LicenseScheme scheme, ReadOnlySpan<byte> publicKey, string licenseKey, string fingerprint, long nowUnixSeconds)
        => VerifyWithClaims(scheme, publicKey, licenseKey, fingerprint, nowUnixSeconds).Machine;

    /// <summary>
    /// As <see cref="VerifyAndDecrypt(LicenseScheme, ReadOnlySpan{byte}, string, string)"/>, also
    /// returning the signed claims. Use this for <c>jti</c> replay detection or <c>kid</c>
    /// key-rotation bookkeeping. Expiry is enforced either way — it is not opt-in.
    /// </summary>
    /// <param name="scheme">The license's signing scheme — drives verifier dispatch.</param>
    /// <param name="publicKey">The account's public key, in the format documented on the type.</param>
    /// <param name="licenseKey">The license key — HKDF input keying material for an encrypted file.</param>
    /// <param name="fingerprint">The target machine's fingerprint — HKDF <c>info</c> for an encrypted file.</param>
    /// <param name="nowUnixSeconds">The current time, seconds since the Unix epoch, used for the <c>exp</c> check.</param>
    /// <returns>The machine carried by the file, together with the claims that were inside the signed bytes.</returns>
    public (Machine Machine, LicenseFileClaims Claims) VerifyWithClaims(
        LicenseScheme scheme,
        ReadOnlySpan<byte> publicKey,
        string licenseKey,
        string fingerprint,
        long nowUnixSeconds)
    {
        // Up front, before anything is parsed: this scheme has no machine-file verifier and never
        // will. `alg` cannot tell it apart from RSA_2048_PKCS1_SIGN — both serialize to
        // `rsa-sha256` — so the refusal has to come from the caller's own scheme value.
        RejectJwtRs256(scheme);

        if (!Verify(scheme, publicKey))
        {
            throw new SignatureVerificationException("Machine file signature verification failed — the file may be forged or corrupted.");
        }

        // Only now is `enc` known to be authentic, so only now may any of it be decoded.
        var isEncrypted = ParseIsEncrypted(Certificate.Alg, scheme);

        var jsonBytes = isEncrypted
            ? DecryptPayload(Certificate.Enc, licenseKey, fingerprint)
            : DecodePlainPayload(Certificate.Enc);

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

        // Second line behind the alg gate: a v2 file must not reach the expiry check with nothing
        // to check.
        if (payload.Meta is null)
        {
            throw new OfflineFileFormatException(
                "Machine file payload is missing the signed 'meta' claims (this looks like a pre-v2 file).");
        }

        // The signature proves the file is authentic. It does not prove it is still valid — that
        // is this check, and skipping it is what made every checked-out machine file permanent.
        // A file checked out with no `ttl` legitimately carries no `exp` and never expires; that
        // absence is by design (check_out_machine.rs sets `exp` from `ttl.map(..)`), not an error.
        // Tolerance is shared with the license-file path on purpose — see the constant's remarks.
        if (payload.Meta.ExpiresAt is { } exp && nowUnixSeconds - LicenseFile.ClockSkewToleranceSeconds > exp)
        {
            throw new LicenseFileExpiredException(exp);
        }

        return (Machine.FromResource(payload.Data), payload.Meta);
    }

    private static void RejectJwtRs256(LicenseScheme scheme)
    {
        if (scheme == LicenseScheme.Rsa2048JwtRs256)
        {
            throw new SchemeNotSupportedException(
                "RSA_2048_JWT_RS256 is rejected server-side for machine files (422 SCHEME_NOT_SUPPORTED) and is not implemented client-side either — this SDK never attempts JWT/RS256 verification.");
        }
    }

    /// <summary>
    /// Parses <c>alg</c> as <c>&lt;encoding&gt;+&lt;signing-suffix&gt;+v2</c> and returns whether
    /// the payload is encrypted, rejecting anything that is not exactly that grammar.
    /// </summary>
    /// <remarks>
    /// Split at the FIRST <c>+</c> for the encoding and the LAST <c>+</c> for the version marker;
    /// the suffix is what remains in between. Anchoring on both ends is what makes this immune to
    /// the hyphens inside <c>aes-256-gcm</c>, <c>rsa-pss-sha256</c> and <c>ecdsa-p256</c>, and to
    /// a padded lookalike such as <c>xbase64+ed25519+v2junk</c> that a substring test waves
    /// through.
    /// </remarks>
    private static bool ParseIsEncrypted(string alg, LicenseScheme scheme)
    {
        var firstPlus = alg.IndexOf('+');
        var lastPlus = alg.LastIndexOf('+');
        if (firstPlus <= 0 || lastPlus <= firstPlus)
        {
            throw new UnsupportedAlgorithmException(
                $"Unsupported machine file algorithm: '{alg}'. Expected '<encoding>+<signing-suffix>+{FormatVersionMarker}'.");
        }

        var version = alg[(lastPlus + 1)..];
        if (!string.Equals(version, FormatVersionMarker, StringComparison.Ordinal))
        {
            throw new UnsupportedAlgorithmException(
                $"Unsupported machine file algorithm: '{alg}'. Only format {FormatVersionMarker} is accepted; a pre-v2 file carries no signed expiry and derives its key without HKDF.");
        }

        var signingSuffix = alg[(firstPlus + 1)..lastPlus];
        var expectedSuffix = ExpectedSigningSuffix(scheme);
        if (!string.Equals(signingSuffix, expectedSuffix, StringComparison.Ordinal))
        {
            throw new UnsupportedAlgorithmException(
                $"Machine file algorithm '{alg}' declares signing suffix '{signingSuffix}', but the license scheme {scheme} signs with '{expectedSuffix}'.");
        }

        var encoding = alg[..firstPlus];
        return encoding switch
        {
            EncryptedEncodingPrefix => true,
            PlainEncodingPrefix => false,
            _ => throw new UnsupportedAlgorithmException(
                $"Unsupported machine file algorithm: '{alg}'. Encoding prefix must be exactly '{PlainEncodingPrefix}' or '{EncryptedEncodingPrefix}'."),
        };
    }

    /// <summary>
    /// The <c>alg</c> signing suffix the server emits for a given scheme
    /// (<c>machine_file.rs:119-126</c>). Note <c>rsa-sha256</c> covers BOTH
    /// <see cref="LicenseScheme.Rsa2048Pkcs1Sign"/> and <see cref="LicenseScheme.Rsa2048JwtRs256"/>
    /// server-side, which is why this map is only ever used to reject a contradiction and never to
    /// pick a verifier.
    /// </summary>
    private static string ExpectedSigningSuffix(LicenseScheme scheme) => scheme switch
    {
        LicenseScheme.None or LicenseScheme.Ed25519Sign => "ed25519",
        LicenseScheme.EcdsaP256Sign => "ecdsa-p256",
        LicenseScheme.Rsa2048Pkcs1Sign => "rsa-sha256",
        LicenseScheme.Rsa2048Pkcs1PssSign => "rsa-pss-sha256",
        _ => throw new UnsupportedAlgorithmException($"Unsupported license scheme for machine file verification: {scheme}."),
    };

    private static byte[] DecodePlainPayload(string enc)
    {
        try
        {
            return Convert.FromBase64String(enc);
        }
        catch (FormatException ex)
        {
            throw new OfflineFileFormatException($"Machine file 'enc' is not valid base64: {ex.Message}");
        }
    }

    /// <summary>
    /// Splits an encrypted <c>enc</c> into its two base64 halves, decodes each independently, and
    /// opens the AES-256-GCM box under the HKDF-derived key.
    /// </summary>
    /// <remarks>
    /// The halves are separately encoded, so the whole string is NOT valid base64 and decoding it
    /// as one blob throws before anything useful happens. The ciphertext half already carries the
    /// 16-byte GCM tag appended by the server's <c>seal_in_place_append_tag</c>.
    /// </remarks>
    private static byte[] DecryptPayload(string enc, string licenseKey, string fingerprint)
    {
        var separator = enc.IndexOf(EncryptedPartSeparator);
        if (separator < 0)
        {
            throw new OfflineFileFormatException(
                $"Encrypted machine file 'enc' is missing its '{EncryptedPartSeparator}' separator: expected '<nonce_b64>{EncryptedPartSeparator}<ciphertext_b64>'.");
        }

        // Standard base64 has no '.', so a second one cannot come from the server's encoder.
        if (enc.IndexOf(EncryptedPartSeparator, separator + 1) >= 0)
        {
            throw new OfflineFileFormatException(
                $"Encrypted machine file 'enc' has more than one '{EncryptedPartSeparator}' separator.");
        }

        var nonce = DecodeHalf(enc[..separator], "nonce");
        var ciphertextAndTag = DecodeHalf(enc[(separator + 1)..], "ciphertext");

        if (nonce.Length != AesGcmCipher.NonceLength)
        {
            throw new OfflineFileFormatException(
                $"Encrypted machine file nonce is {nonce.Length} bytes; expected {AesGcmCipher.NonceLength}.");
        }

        if (ciphertextAndTag.Length < AesGcmCipher.TagLength)
        {
            throw new OfflineFileFormatException(
                $"Encrypted machine file ciphertext is {ciphertextAndTag.Length} bytes; expected at least the {AesGcmCipher.TagLength}-byte GCM tag.");
        }

        var ciphertext = ciphertextAndTag.AsSpan(0, ciphertextAndTag.Length - AesGcmCipher.TagLength);
        var tag = ciphertextAndTag.AsSpan(ciphertextAndTag.Length - AesGcmCipher.TagLength);

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

    private static byte[] DecodeHalf(string half, string what)
    {
        try
        {
            return Convert.FromBase64String(half);
        }
        catch (FormatException ex)
        {
            throw new OfflineFileFormatException($"Encrypted machine file {what} is not valid base64: {ex.Message}");
        }
    }
}
