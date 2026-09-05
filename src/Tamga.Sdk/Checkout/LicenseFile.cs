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
/// <see cref="LicenseFileClaims"/>). A pre-v2 file is rejected outright by <see cref="Parse"/>,
/// before any key or signature work — see <see cref="Parse"/>.
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

    /// <summary>The mandatory format-version marker after the last <c>+</c> of <c>alg</c>.</summary>
    private const string FormatVersionMarker = "v2";

    /// <summary>The <c>alg</c> encoding prefix for a plain (unencrypted) payload.</summary>
    private const string PlainEncodingPrefix = "base64";

    /// <summary>The <c>alg</c> encoding prefix for an AES-256-GCM payload.</summary>
    private const string EncryptedEncodingPrefix = "aes-256-gcm";

    /// <summary>The only signing suffix a license file can carry — license files are Ed25519-signed regardless of the license's scheme.</summary>
    private const string SigningSuffix = "ed25519";

    /// <summary>The parsed, unverified <c>{enc, sig, alg}</c> certificate.</summary>
    public LicenseFileCertificate Certificate { get; }

    private LicenseFile(LicenseFileCertificate certificate)
    {
        Certificate = certificate;
    }

    /// <summary>
    /// Parses a PEM-wrapped <c>.lic</c> file and enforces its format: <c>alg</c> must be exactly
    /// <c>base64+ed25519+v2</c> or <c>aes-256-gcm+ed25519+v2</c>. Does NOT verify the signature —
    /// call <see cref="Verify"/> or <see cref="VerifyAndDecrypt(System.ReadOnlySpan{byte}, string)"/> separately.
    /// </summary>
    /// <remarks>
    /// The format gate lives here, ahead of every verifying entry point, so a pre-v2 or
    /// non-Ed25519 file is refused with one exception type before any key or signature work — not
    /// with whichever of three exceptions the first method to run happened to hit (audit D17).
    /// <c>alg</c> is outside the signature and therefore attacker-malleable, which is why it is
    /// gated rather than trusted; nothing here reads <c>enc</c>.
    /// </remarks>
    /// <exception cref="OfflineFileFormatException">The PEM envelope or inner JSON is malformed.</exception>
    /// <exception cref="UnsupportedAlgorithmException"><c>alg</c> is not one of the two format-v2, Ed25519 values above — including every pre-v2 file.</exception>
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

        if (cert is null)
        {
            throw new OfflineFileFormatException("License file certificate JSON was null.");
        }

        // The format gate, before any verifier exists to run. Result discarded: the same parser
        // decides plain-vs-encrypted later, from the same string.
        _ = ParseIsEncrypted(cert.Alg);

        return new LicenseFile(cert);
    }

    /// <summary>
    /// Verifies the Ed25519 signature against the account's public key. Returns
    /// <see langword="true"/>/<see langword="false"/> rather than throwing — callers that need a
    /// fail-closed exception should use <see cref="VerifyAndDecrypt(System.ReadOnlySpan{byte}, string)"/>.
    /// </summary>
    /// <remarks>Signature only. <c>alg</c> was already enforced by <see cref="Parse"/>, so every instance this runs on is a format-v2 Ed25519 file.</remarks>
    public bool Verify(ReadOnlySpan<byte> publicKey)
    {
        if (!TryDecodeSignature(out var signature))
        {
            return false;
        }

        // CRITICAL: sign/verify over `enc`'s base64 STRING bytes (UTF-8 of the string itself), NOT
        // the base64-decoded payload bytes. This is the single most consequential correctness trap
        // in this SDK — see type-level remarks above.
        var message = Encoding.UTF8.GetBytes(Certificate.Enc);
        return Ed25519.Verify(publicKey, message, signature);
    }

    /// <summary>Base64-decodes <c>sig</c>; <see langword="false"/> (never a throw) when it is not base64, which every caller treats as a failed verification.</summary>
    private bool TryDecodeSignature(out byte[] signature)
    {
        try
        {
            signature = Convert.FromBase64String(Certificate.Sig);
            return true;
        }
        catch (FormatException)
        {
            signature = Array.Empty<byte>();
            return false;
        }
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
    /// <exception cref="SignatureVerificationException">Signature verification failed — the file may be forged or corrupted. As its subclass <see cref="LicenseKeyMismatchException"/>: the signature verified and the encrypted payload did not open under the supplied key material.</exception>
    /// <exception cref="UnsupportedAlgorithmException">Cannot occur on a parsed instance — <see cref="Parse"/> refuses any <c>alg</c> that is not format-v2 Ed25519; listed so a caller catching it around Parse sees the same type here.</exception>
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
    /// <exception cref="SignatureVerificationException">Signature verification failed — the file may be forged or corrupted. As its subclass <see cref="LicenseKeyMismatchException"/>: the signature verified and the encrypted payload did not open under the supplied key material.</exception>
    /// <exception cref="UnsupportedAlgorithmException">Cannot occur on a parsed instance — <see cref="Parse"/> refuses any <c>alg</c> that is not format-v2 Ed25519; listed so a caller catching it around Parse sees the same type here.</exception>
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

        var payload = ParsePayload(DecodePayloadJson(licenseKey));
        return FinishPayload(payload, nowUnixSeconds);
    }

    /// <summary>
    /// Verifies a <c>.lic</c> file against a <see cref="SigningKeySet"/>, trying every trusted key
    /// against the signature so a file that predates a key rotation still verifies, and returns
    /// the license, its signed claims and the key that verified it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The outcomes, and what each asks of the caller:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// a held key verifies the signature → the license, its claims and that key are returned;
    /// </description></item>
    /// <item><description>
    /// no key verifies and the <c>kid</c> is NOT in the set → <see cref="UnknownSigningKeyException"/> (or
    /// <see cref="UnpublishedSigningKeyException"/>). <b>Not a forgery</b> — refresh the key set or
    /// ship an update, then retry;
    /// </description></item>
    /// <item><description>
    /// no key verifies and the <c>kid</c> IS in the set, or cannot be read →
    /// <see cref="SignatureVerificationException"/>. Refuse the file;
    /// </description></item>
    /// <item><description>
    /// a key verifies but the encrypted payload will not open →
    /// <see cref="LicenseKeyMismatchException"/>. The file is authentic; the license key is wrong.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>Order of work.</b> The signature is checked first — against every usable key in
    /// <paramref name="signingKeys"/>, over <c>enc</c>'s base64 string — and nothing in <c>enc</c>
    /// is decoded until one key has verified it, exactly like the single-key overload. Only when no
    /// key verifies is <c>enc</c> decoded (and, when encrypted, decrypted under the license key)
    /// purely to read the <c>kid</c> and label the failure; nothing else is taken from those bytes
    /// and no <see cref="License"/> is ever produced from them. Before 2.1.2 this path decoded
    /// first to select the one key the <c>kid</c> named (audit D16); the <c>kid</c> now only
    /// decides the failure label, so "stale set" and "forgery" stay distinct without gating
    /// which key may verify.
    /// </para>
    /// </remarks>
    /// <param name="signingKeys">The trusted key set — from <c>TamgaClient.GetSigningKeySetAsync</c> or <see cref="SigningKeySet.FromPublicKeys(string[])"/>.</param>
    /// <param name="licenseKey">The license key, used to derive the AES-256-GCM key for an encrypted file.</param>
    /// <param name="nowUnixSeconds">The current time, seconds since the Unix epoch, used for the <c>exp</c> check.</param>
    /// <returns>The license, the signed claims, and the key that verified the signature.</returns>
    /// <exception cref="UnknownSigningKeyException">No key verified and the file's <c>kid</c> names no key in <paramref name="signingKeys"/> — refresh the set; this is not a signature failure.</exception>
    /// <exception cref="UnpublishedSigningKeyException">No key verified and the file's <c>kid</c> is <see cref="Crypto.Ed25519.UnpublishedAccountKeyId"/>.</exception>
    /// <exception cref="SignatureVerificationException">No key verified and the <c>kid</c> names a held key or is unreadable — the file is forged or corrupted.</exception>
    /// <exception cref="LicenseKeyMismatchException">A key verified; the AES-256-GCM payload did not open under <paramref name="licenseKey"/>.</exception>
    /// <exception cref="LicenseFileExpiredException">The signed <c>exp</c> claim has passed, allowing 60 seconds of clock skew.</exception>
    /// <exception cref="OfflineFileFormatException">The (verified) payload is malformed or carries no signed <c>meta</c> claims.</exception>
    public (License License, LicenseFileClaims Claims, SigningKey Key) VerifyWithKeySet(
        SigningKeySet signingKeys,
        string licenseKey,
        long nowUnixSeconds)
    {
        ArgumentNullException.ThrowIfNull(signingKeys);

        // Signature first, against every usable key, over `enc`'s base64 STRING bytes — before a
        // single byte of `enc` is decoded. Nothing attacker-controlled is parsed until one held
        // key has vouched for it. (alg was gated by Parse.)
        SigningKey? verifiedKey = null;
        if (TryDecodeSignature(out var signature))
        {
            var message = Encoding.UTF8.GetBytes(Certificate.Enc);
            verifiedKey = signingKeys.FindVerifyingKey(publicKey => Ed25519.Verify(publicKey, message, signature));
        }

        if (verifiedKey is null)
        {
            // No held key vouches for this file. The only remaining question is how to label
            // that, and the kid decides — read from bytes nobody has verified, used for nothing
            // but choosing between failure messages.
            throw signingKeys.UnverifiableFileFailure(TryReadUnverifiedKeyId(licenseKey), "License file");
        }

        // Only now is `enc` known to be authentic. From here an AES-GCM failure cannot be a
        // forgery — the signature just proved the ciphertext is the server's — so DecryptPayload
        // reports it as LicenseKeyMismatchException.
        var payload = ParsePayload(DecodePayloadJson(licenseKey));
        var (license, verifiedClaims) = FinishPayload(payload, nowUnixSeconds);
        return (license, verifiedClaims, verifiedKey);
    }

    /// <summary>
    /// Reads the <c>kid</c> from a payload NO held key has vouched for, or <see langword="null"/>
    /// when it cannot be read. Used only to label a failure — never to select a key.
    /// </summary>
    private string? TryReadUnverifiedKeyId(string licenseKey)
    {
        try
        {
            var keyId = ParsePayload(DecodePayloadJson(licenseKey)).Meta!.KeyId;
            return string.IsNullOrEmpty(keyId) ? null : keyId;
        }
        catch (Exception ex) when (ex is OfflineFileFormatException or UnsupportedAlgorithmException or SignatureVerificationException)
        {
            // Undecodable, pre-v2, or undecryptable (a wrong license key surfaces from
            // DecryptPayload as LicenseKeyMismatchException, which is a
            // SignatureVerificationException): there is no kid to label with.
            return null;
        }
    }

    /// <summary>
    /// As <see cref="VerifyWithKeySet"/>, using the local clock and returning just the license.
    /// </summary>
    /// <param name="signingKeys">The trusted key set.</param>
    /// <param name="licenseKey">The license key, used to derive the AES-256-GCM key for an encrypted file.</param>
    public License VerifyAndDecrypt(SigningKeySet signingKeys, string licenseKey)
        => VerifyAndDecrypt(signingKeys, licenseKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    /// <summary>
    /// As <see cref="VerifyAndDecrypt(SigningKeySet, string)"/>, with the current time supplied by
    /// the caller — see <see cref="VerifyAndDecrypt(ReadOnlySpan{byte}, string, long)"/> for why
    /// that matters.
    /// </summary>
    /// <param name="signingKeys">The trusted key set.</param>
    /// <param name="licenseKey">The license key, used to derive the AES-256-GCM key for an encrypted file.</param>
    /// <param name="nowUnixSeconds">The current time, seconds since the Unix epoch.</param>
    public License VerifyAndDecrypt(SigningKeySet signingKeys, string licenseKey, long nowUnixSeconds)
        => VerifyWithKeySet(signingKeys, licenseKey, nowUnixSeconds).License;

    /// <summary>
    /// Decodes <c>enc</c> to the payload JSON bytes: base64-decode, then decrypt or pass through
    /// by <c>alg</c>.
    /// </summary>
    /// <remarks>
    /// One implementation for the single-key and key-set paths. The <c>alg</c> parser it calls is
    /// the same one <see cref="Parse"/> ran, so the gate cannot drift between construction and use.
    /// </remarks>
    private byte[] DecodePayloadJson(string licenseKey)
    {
        var isEncrypted = ParseIsEncrypted(Certificate.Alg);

        byte[] payloadBytes;
        try
        {
            payloadBytes = Convert.FromBase64String(Certificate.Enc);
        }
        catch (FormatException ex)
        {
            throw new OfflineFileFormatException($"License file 'enc' is not valid base64: {ex.Message}");
        }

        return isEncrypted ? DecryptPayload(payloadBytes, licenseKey) : payloadBytes;
    }

    /// <summary>
    /// Parses <c>alg</c> as <c>&lt;encoding&gt;+ed25519+v2</c> and returns whether the payload is
    /// encrypted, rejecting anything that is not exactly that grammar.
    /// </summary>
    /// <remarks>
    /// Anchored at the FIRST and LAST <c>+</c>, like <see cref="MachineFile"/>'s parser, so
    /// <c>xbase64+ed25519+v2</c>, <c>base64+ed25519+v2junk</c> and <c>base64+ed25519</c> are all
    /// refused — the substring tests this replaced (<c>Contains("ed25519")</c>,
    /// <c>EndsWith("+v2")</c>, <c>Contains("aes-256-gcm")</c>) waved every one of those through.
    /// The <c>+v2</c> marker is load-bearing: a v1 file carried no expiry inside its signature, so
    /// accepting one hands back the permanent-file problem v2 exists to close.
    /// </remarks>
    private static bool ParseIsEncrypted(string alg)
    {
        var firstPlus = alg.IndexOf('+');
        var lastPlus = alg.LastIndexOf('+');
        if (firstPlus <= 0 || lastPlus <= firstPlus)
        {
            throw new UnsupportedAlgorithmException(
                $"Unsupported license file algorithm: '{alg}'. Expected '{PlainEncodingPrefix}+{SigningSuffix}+{FormatVersionMarker}' or '{EncryptedEncodingPrefix}+{SigningSuffix}+{FormatVersionMarker}'.");
        }

        var version = alg[(lastPlus + 1)..];
        if (!string.Equals(version, FormatVersionMarker, StringComparison.Ordinal))
        {
            throw new UnsupportedAlgorithmException(
                $"Unsupported license file algorithm: '{alg}'. Only format {FormatVersionMarker} is accepted; a pre-v2 file carries no signed expiry.");
        }

        var signingSuffix = alg[(firstPlus + 1)..lastPlus];
        if (!string.Equals(signingSuffix, SigningSuffix, StringComparison.Ordinal))
        {
            throw new UnsupportedAlgorithmException(
                $"Unsupported license file algorithm: '{alg}'. Only {SigningSuffix}-signed license files exist; the signing suffix is '{signingSuffix}'.");
        }

        var encoding = alg[..firstPlus];
        return encoding switch
        {
            EncryptedEncodingPrefix => true,
            PlainEncodingPrefix => false,
            _ => throw new UnsupportedAlgorithmException(
                $"Unsupported license file algorithm: '{alg}'. Encoding prefix must be exactly '{PlainEncodingPrefix}' or '{EncryptedEncodingPrefix}'."),
        };
    }

    /// <summary>Deserializes the payload JSON and rejects a payload carrying no signed claims.</summary>
    private static LicenseFilePayload ParsePayload(byte[] jsonBytes)
    {
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

        return payload;
    }

    /// <summary>Enforces the signed <c>exp</c> claim and maps the resource. Runs only after a signature has verified.</summary>
    private static (License License, LicenseFileClaims Claims) FinishPayload(LicenseFilePayload payload, long nowUnixSeconds)
    {
        var meta = payload.Meta!;

        // The signature proves the file is authentic. It does not prove it is still valid — that
        // is this check, and skipping it is what made v1 files permanent.
        if (meta.ExpiresAt is { } exp && nowUnixSeconds - ClockSkewToleranceSeconds > exp)
        {
            throw new LicenseFileExpiredException(exp);
        }

        return (License.FromResource(payload.Data), meta);
    }

    /// <summary>
    /// How much clock skew is tolerated when checking <c>exp</c>, for BOTH offline file formats.
    /// </summary>
    /// <remarks>
    /// Deliberately small. The client's clock is under the attacker's control, so a generous
    /// allowance is just a free extension on every expired file; this covers ordinary NTP drift
    /// and nothing more.
    ///
    /// <see cref="MachineFile"/> reads this same constant rather than declaring its own. Two
    /// copies would drift, and the drift would be invisible: one of the two file types would
    /// silently start honouring a different grace period than the other.
    /// </remarks>
    internal const long ClockSkewToleranceSeconds = 60;

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
            // Every caller has verified the signature before reaching here (the one that has not,
            // TryReadUnverifiedKeyId, swallows this). So the ciphertext is the server's and the
            // only thing that can be wrong is the key derived from the caller's license key.
            throw new LicenseKeyMismatchException(
                "License file decryption failed authentication: the signature verified, so the payload is the server's, " +
                "and the AES-256-GCM key derived from the supplied license key does not open it — the license key is wrong for this file.",
                ex);
        }
        catch (CryptographicException ex)
        {
            throw new LicenseKeyMismatchException($"License file decryption failed after a verified signature: {ex.Message}", ex);
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
        // .lic/.machine input. Found in security review.
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
