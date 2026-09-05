using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NSec.Cryptography;
using Tamga.Sdk.Checkout;
using Tamga.Sdk.Crypto;
using Tamga.Sdk.Models;
using Xunit;

namespace Tamga.Sdk.Tests.Checkout;

/// <summary>
/// The defect this round exists to close: an offline file signed BEFORE a key rotation must still
/// verify, and a file naming a key the caller does not hold must NOT be reported as a forgery.
/// </summary>
/// <remarks>
/// Those two are different incidents with opposite responses — one says "refresh the key set", the
/// other says "refuse the customer" — and collapsing them is what locks a paying customer out of a
/// perfectly authentic file while sending support to the wrong place.
/// </remarks>
public class SigningKeyRotationTests
{
    private const long ClockBeforeAnyExpiry = 0;

    private sealed record Signer(string PublicKeyB64, byte[] PublicKey, Key PrivateKey) : IDisposable
    {
        public string Kid => Tamga.Sdk.Crypto.Ed25519.KeyId(PublicKeyB64);

        public void Dispose() => PrivateKey.Dispose();
    }

    private static Signer NewSigner()
    {
        var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var raw = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        return new Signer(Convert.ToBase64String(raw), raw, key);
    }

    private static string LicensePayload(Guid licenseId, string kid, long? exp = null)
    {
        var meta = new JsonObject { ["iat"] = 1767225600L, ["jti"] = "test-jti", ["kid"] = kid };
        if (exp is { } value)
        {
            meta["exp"] = value;
        }

        return new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["type"] = "licenses",
                ["id"] = licenseId.ToString(),
                ["attributes"] = new JsonObject { ["key"] = "LIC-ROTATE-1", ["suspended"] = false, ["uses"] = 1 },
            },
            ["meta"] = meta,
        }.ToJsonString();
    }

    private static string MachinePayload(Guid machineId, string fingerprint, string kid, long? exp = null)
    {
        var meta = new JsonObject { ["iat"] = 1767225600L, ["jti"] = "test-jti", ["kid"] = kid };
        if (exp is { } value)
        {
            meta["exp"] = value;
        }

        return new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["type"] = "machines",
                ["id"] = machineId.ToString(),
                ["attributes"] = new JsonObject { ["fingerprint"] = fingerprint },
            },
            ["meta"] = meta,
        }.ToJsonString();
    }

    private static string BuildLicensePem(Signer signer, string payloadJson, string alg = "base64+ed25519+v2")
    {
        var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson));
        var sig = Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(signer.PrivateKey, Encoding.UTF8.GetBytes(enc)));
        var certJson = JsonSerializer.Serialize(new { enc, sig, alg });
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(certJson));
        return $"-----BEGIN LICENSE FILE-----\n{body}\n-----END LICENSE FILE-----";
    }

    private static string BuildMachinePem(Signer signer, string payloadJson, string alg = "base64+ed25519+v2")
    {
        var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson));
        var sig = Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(signer.PrivateKey, Encoding.UTF8.GetBytes(enc)));
        var certJson = JsonSerializer.Serialize(new { enc, sig, alg });
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(certJson));
        return $"-----BEGIN MACHINE FILE-----\n{body}\n-----END MACHINE FILE-----";
    }

    // ── THE DEFECT ────────────────────────────────────────────────────────────

    /// <summary>
    /// M22, stated directly: a file signed by the OLD key, after the account has rotated to a new
    /// one, still verifies — because the key set carries the retired key and the file's own
    /// <c>kid</c> selects it.
    /// </summary>
    [Fact]
    public void LicenseFile_SignedBeforeARotation_StillVerifiesAgainstTheKeySet()
    {
        using var oldKey = NewSigner();
        using var newKey = NewSigner();

        var licenseId = Guid.NewGuid();
        var pem = BuildLicensePem(oldKey, LicensePayload(licenseId, oldKey.Kid));
        var file = LicenseFile.Parse(pem);

        // Rotation happened: the ACTIVE key is the new one, the old one is retired but published.
        var keySet = SigningKeySet.FromResources(new[]
        {
            SigningKeyResourceFor(newKey.Kid, newKey.PublicKeyB64, "active"),
            SigningKeyResourceFor(oldKey.Kid, oldKey.PublicKeyB64, "retired"),
        });

        var (license, claims, usedKey) = file.VerifyWithKeySet(keySet, "unused-for-plain", ClockBeforeAnyExpiry);

        Assert.Equal(licenseId, license.Id);
        Assert.Equal(oldKey.Kid, claims.KeyId);
        Assert.Equal(oldKey.Kid, usedKey.KeyId);
        Assert.True(usedKey.IsRetired, "the file was signed by the retired key, and that is the point");

        // And the single-key path against the CURRENT key is exactly the lockout this fixes.
        Assert.Throws<SignatureVerificationException>(
            () => file.VerifyAndDecrypt(newKey.PublicKey, "unused-for-plain", ClockBeforeAnyExpiry));
    }

    /// <summary>
    /// The other half: dropping retired keys from the set reintroduces the lockout. This is the
    /// assertion that goes red if <c>FromResources</c> ever starts filtering on
    /// <c>status == "active"</c>.
    /// </summary>
    [Fact]
    public void LicenseFile_SignedBeforeARotation_FailsWhenTheKeySetOmitsTheRetiredKey()
    {
        using var oldKey = NewSigner();
        using var newKey = NewSigner();

        var file = LicenseFile.Parse(BuildLicensePem(oldKey, LicensePayload(Guid.NewGuid(), oldKey.Kid)));
        var activeOnly = SigningKeySet.FromResources(new[] { SigningKeyResourceFor(newKey.Kid, newKey.PublicKeyB64, "active") });

        var ex = Assert.Throws<UnknownSigningKeyException>(
            () => file.VerifyWithKeySet(activeOnly, "unused-for-plain", ClockBeforeAnyExpiry));

        // Still NOT a forgery verdict — the caller is told to refresh, not to refuse.
        Assert.Equal(oldKey.Kid, ex.KeyId);
        Assert.Contains("not a signature failure", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The constraint stated in the brief: a file whose <c>kid</c> matches no key in the set must
    /// fail with a condition DISTINGUISHABLE from "signature is forged".
    /// </summary>
    [Fact]
    public void LicenseFile_UnknownKid_IsDistinguishableFromAForgedSignature()
    {
        using var unknownKey = NewSigner();
        using var trustedKey = NewSigner();

        var authentic = LicenseFile.Parse(BuildLicensePem(unknownKey, LicensePayload(Guid.NewGuid(), unknownKey.Kid)));
        var keySet = SigningKeySet.FromPublicKeys(trustedKey.PublicKeyB64);

        // (a) authentic file, key not held → selection failure, NOT a signature failure.
        var selection = Assert.Throws<UnknownSigningKeyException>(
            () => authentic.VerifyWithKeySet(keySet, "unused-for-plain", ClockBeforeAnyExpiry));
        Assert.IsAssignableFrom<SigningKeySelectionException>(selection);
        Assert.IsNotType<SignatureVerificationException>(selection);

        // (b) a file naming a key we DO hold, whose signature was made by another key → forged.
        var forged = LicenseFile.Parse(BuildLicensePem(unknownKey, LicensePayload(Guid.NewGuid(), trustedKey.Kid)));
        var forgery = Assert.Throws<SignatureVerificationException>(
            () => forged.VerifyWithKeySet(keySet, "unused-for-plain", ClockBeforeAnyExpiry));
        Assert.IsNotAssignableFrom<SigningKeySelectionException>(forgery);
        Assert.Contains("forged or corrupted", forgery.Message, StringComparison.Ordinal);
        Assert.IsNotType<LicenseKeyMismatchException>(forgery);
    }

    /// <summary>
    /// An account whose key column was never populated signs every file with
    /// <c>e3b0c44298fc1c14</c>. That is "this server published no key at all", not "your key set is
    /// stale", and the two must not read the same.
    /// </summary>
    [Fact]
    public void LicenseFile_SignedByAnUnbackfilledAccount_ReportsItsOwnCondition()
    {
        using var signer = NewSigner();
        using var trusted = NewSigner();

        var file = LicenseFile.Parse(BuildLicensePem(signer, LicensePayload(Guid.NewGuid(), Tamga.Sdk.Crypto.Ed25519.UnpublishedAccountKeyId)));
        var keySet = SigningKeySet.FromPublicKeys(trusted.PublicKeyB64);

        var ex = Assert.Throws<UnpublishedSigningKeyException>(
            () => file.VerifyWithKeySet(keySet, "unused-for-plain", ClockBeforeAnyExpiry));

        Assert.Equal("e3b0c44298fc1c14", ex.KeyId);
        Assert.IsNotType<UnknownSigningKeyException>(ex);
        Assert.Contains("no Ed25519 public key", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every usable key is tried against the signature, so a file verifies against whichever
    /// held key signed it — the <c>kid</c> no longer gates which key may verify, it only labels a
    /// failure. Here the claim names <c>other</c> while <c>signer</c> made the signature, and both
    /// are held: the file is authentic under a trusted key, so it is good, and the key returned is
    /// the one that actually verified it.
    /// </summary>
    [Fact]
    public void LicenseFile_VerifiesAgainstWhicheverHeldKeySigned_EvenWhenTheKidNamesAnotherHeldKey()
    {
        using var signer = NewSigner();
        using var other = NewSigner();

        var file = LicenseFile.Parse(BuildLicensePem(signer, LicensePayload(Guid.NewGuid(), other.Kid)));
        var keySet = SigningKeySet.FromPublicKeys(signer.PublicKeyB64, other.PublicKeyB64);

        var (license, claims, usedKey) = file.VerifyWithKeySet(keySet, "unused-for-plain", ClockBeforeAnyExpiry);

        Assert.Equal("LIC-ROTATE-1", license.Key);
        Assert.Equal(other.Kid, claims.KeyId);
        Assert.Equal(signer.Kid, usedKey.KeyId);
    }

    /// <summary>The signed <c>exp</c> claim is enforced on the key-set path too — it is not opt-in.</summary>
    [Fact]
    public void LicenseFile_KeySetPath_StillEnforcesTheSignedExpiry()
    {
        using var signer = NewSigner();
        var file = LicenseFile.Parse(BuildLicensePem(signer, LicensePayload(Guid.NewGuid(), signer.Kid, exp: 1_000)));
        var keySet = SigningKeySet.FromPublicKeys(signer.PublicKeyB64);

        Assert.Throws<LicenseFileExpiredException>(() => file.VerifyWithKeySet(keySet, "unused-for-plain", 2_000));

        // ...and the same file is fine before its expiry.
        Assert.Equal(signer.Kid, file.VerifyWithKeySet(keySet, "unused-for-plain", 500).Claims.KeyId);
    }

    /// <summary>An encrypted file works the same way — the kid is read after AES-GCM authenticates the payload.</summary>
    [Fact]
    public void LicenseFile_KeySetPath_HandlesAnEncryptedFile()
    {
        using var signer = NewSigner();
        const string licenseKey = "super-secret-license-key";

        var plaintext = Encoding.UTF8.GetBytes(LicensePayload(Guid.NewGuid(), signer.Kid));
        var nonce = new byte[AesGcmCipher.NonceLength];
        System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);
        var aesKey = Hkdf.DeriveLicenseFileKey(licenseKey);
        var (ciphertext, tag) = AesGcmCipher.Seal(aesKey, nonce, plaintext);

        var payload = new byte[nonce.Length + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, payload, nonce.Length, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length + ciphertext.Length, tag.Length);

        var enc = Convert.ToBase64String(payload);
        var sig = Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(signer.PrivateKey, Encoding.UTF8.GetBytes(enc)));
        var certJson = JsonSerializer.Serialize(new { enc, sig, alg = "aes-256-gcm+ed25519+v2" });
        var pem = $"-----BEGIN LICENSE FILE-----\n{Convert.ToBase64String(Encoding.UTF8.GetBytes(certJson))}\n-----END LICENSE FILE-----";

        var keySet = SigningKeySet.FromPublicKeys(signer.PublicKeyB64);
        var license = LicenseFile.Parse(pem).VerifyAndDecrypt(keySet, licenseKey, ClockBeforeAnyExpiry);

        Assert.Equal("LIC-ROTATE-1", license.Key);
    }

    [Fact]
    public void LicenseFile_KeySetPath_RejectsANullKeySet()
    {
        using var signer = NewSigner();
        var file = LicenseFile.Parse(BuildLicensePem(signer, LicensePayload(Guid.NewGuid(), signer.Kid)));

        Assert.Throws<ArgumentNullException>(() => file.VerifyWithKeySet(null!, "unused", ClockBeforeAnyExpiry));
    }

    // ── D16: verify first; label with the kid; wrong key material is not a forgery ────────────

    private static string EncryptedLicensePem(Signer signer, string payloadJson, string licenseKey)
    {
        var plaintext = Encoding.UTF8.GetBytes(payloadJson);
        var nonce = new byte[AesGcmCipher.NonceLength];
        System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);
        var (ciphertext, tag) = AesGcmCipher.Seal(Hkdf.DeriveLicenseFileKey(licenseKey), nonce, plaintext);

        var payload = new byte[nonce.Length + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, payload, nonce.Length, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length + ciphertext.Length, tag.Length);

        var enc = Convert.ToBase64String(payload);
        var sig = Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(signer.PrivateKey, Encoding.UTF8.GetBytes(enc)));
        var certJson = JsonSerializer.Serialize(new { enc, sig, alg = "aes-256-gcm+ed25519+v2" });
        return $"-----BEGIN LICENSE FILE-----\n{Convert.ToBase64String(Encoding.UTF8.GetBytes(certJson))}\n-----END LICENSE FILE-----";
    }

    /// <summary>
    /// The signature is checked before a byte of <c>enc</c> is decoded. A garbage <c>enc</c> under
    /// a signature no held key made is a signature failure — NOT a format error, which would prove
    /// attacker-controlled bytes had been parsed first. This is the assertion the old
    /// decode-first order fails.
    /// </summary>
    [Fact]
    public void LicenseFile_KeySetPath_VerifiesBeforeDecoding_SoAnUnverifiableGarbageEncIsASignatureFailure()
    {
        using var stranger = NewSigner();
        using var trusted = NewSigner();

        const string garbageEnc = "!!! not base64 !!!";
        var sig = Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(stranger.PrivateKey, Encoding.UTF8.GetBytes(garbageEnc)));
        var certJson = JsonSerializer.Serialize(new { enc = garbageEnc, sig, alg = "base64+ed25519+v2" });
        var file = LicenseFile.Parse($"-----BEGIN LICENSE FILE-----\n{Convert.ToBase64String(Encoding.UTF8.GetBytes(certJson))}\n-----END LICENSE FILE-----");

        var ex = Assert.Throws<SignatureVerificationException>(
            () => file.VerifyWithKeySet(SigningKeySet.FromPublicKeys(trusted.PublicKeyB64), "unused", ClockBeforeAnyExpiry));

        // The kid was unreadable, so the failure is labelled a signature failure — and it is
        // exactly the base type, never the wrong-key subclass.
        Assert.IsType<SignatureVerificationException>(ex);
        Assert.Contains("could not be read", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// After a good signature the ciphertext is the server's, so an AES-GCM tag failure can only
    /// mean the wrong license key. That is its own type — still a SignatureVerificationException
    /// for every catch clause written against 2.1.1, and never a key-selection failure.
    /// </summary>
    [Fact]
    public void LicenseFile_KeySetPath_ReportsAWrongLicenseKey_AsLicenseKeyMismatch_NotAsAForgery()
    {
        using var signer = NewSigner();
        var file = LicenseFile.Parse(EncryptedLicensePem(signer, LicensePayload(Guid.NewGuid(), signer.Kid), "right-key"));
        var keySet = SigningKeySet.FromPublicKeys(signer.PublicKeyB64);

        var ex = Assert.Throws<LicenseKeyMismatchException>(() => file.VerifyWithKeySet(keySet, "wrong-key", ClockBeforeAnyExpiry));

        Assert.IsAssignableFrom<SignatureVerificationException>(ex);
        Assert.IsNotAssignableFrom<SigningKeySelectionException>(ex);
        Assert.NotNull(ex.InnerException);

        // And the same file opens with the right key.
        Assert.Equal("LIC-ROTATE-1", file.VerifyAndDecrypt(keySet, "right-key", ClockBeforeAnyExpiry).Key);
    }

    /// <summary>An encrypted file from a stale key set, correct license key: the kid is read (AES-GCM authenticates it) and says "unknown key", as before.</summary>
    [Fact]
    public void LicenseFile_KeySetPath_EncryptedFile_StaleKeySet_CorrectLicenseKey_IsUnknownKey()
    {
        using var signer = NewSigner();
        using var trusted = NewSigner();
        var file = LicenseFile.Parse(EncryptedLicensePem(signer, LicensePayload(Guid.NewGuid(), signer.Kid), "right-key"));

        var ex = Assert.Throws<UnknownSigningKeyException>(
            () => file.VerifyWithKeySet(SigningKeySet.FromPublicKeys(trusted.PublicKeyB64), "right-key", ClockBeforeAnyExpiry));

        Assert.Equal(signer.Kid, ex.KeyId);
        Assert.Equal(new[] { trusted.Kid }, ex.AvailableKeyIds);
    }

    /// <summary>
    /// An encrypted file from a stale key set AND the wrong license key: no key verifies and the
    /// kid cannot be read, so the only honest label is a signature failure. Not
    /// LicenseKeyMismatch — that claims a verified signature — and not UnknownSigningKey — no kid
    /// was read to name.
    /// </summary>
    [Fact]
    public void LicenseFile_KeySetPath_EncryptedFile_StaleKeySet_WrongLicenseKey_IsASignatureFailure()
    {
        using var signer = NewSigner();
        using var trusted = NewSigner();
        var file = LicenseFile.Parse(EncryptedLicensePem(signer, LicensePayload(Guid.NewGuid(), signer.Kid), "right-key"));

        var ex = Assert.Throws<SignatureVerificationException>(
            () => file.VerifyWithKeySet(SigningKeySet.FromPublicKeys(trusted.PublicKeyB64), "wrong-key", ClockBeforeAnyExpiry));

        Assert.IsType<SignatureVerificationException>(ex);
    }

    [Fact]
    public void MachineFile_KeySetPath_VerifiesBeforeDecoding_SoAnUnverifiableGarbageEncIsASignatureFailure()
    {
        using var stranger = NewSigner();
        using var trusted = NewSigner();

        const string garbageEnc = "!!! not base64 !!!";
        var sig = Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(stranger.PrivateKey, Encoding.UTF8.GetBytes(garbageEnc)));
        var certJson = JsonSerializer.Serialize(new { enc = garbageEnc, sig, alg = "base64+ed25519+v2" });
        var file = MachineFile.Parse($"-----BEGIN MACHINE FILE-----\n{Convert.ToBase64String(Encoding.UTF8.GetBytes(certJson))}\n-----END MACHINE FILE-----");

        var ex = Assert.Throws<SignatureVerificationException>(() => file.VerifyWithKeySet(
            LicenseScheme.Ed25519Sign, SigningKeySet.FromPublicKeys(trusted.PublicKeyB64), "unused", "fp", ClockBeforeAnyExpiry));

        Assert.IsType<SignatureVerificationException>(ex);
    }

    [Fact]
    public void MachineFile_KeySetPath_ReportsAWrongFingerprint_AsLicenseKeyMismatch_NotAsAForgery()
    {
        using var signer = NewSigner();
        const string fingerprint = "fp-d16";
        const string licenseKey = "the-license-key";

        var plaintext = Encoding.UTF8.GetBytes(MachinePayload(Guid.NewGuid(), fingerprint, signer.Kid));
        var nonce = new byte[AesGcmCipher.NonceLength];
        System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);
        var (ciphertext, tag) = AesGcmCipher.Seal(Hkdf.DeriveMachineFileKey(licenseKey, fingerprint), nonce, plaintext);
        var ciphertextAndTag = new byte[ciphertext.Length + tag.Length];
        Buffer.BlockCopy(ciphertext, 0, ciphertextAndTag, 0, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, ciphertextAndTag, ciphertext.Length, tag.Length);
        var enc = $"{Convert.ToBase64String(nonce)}.{Convert.ToBase64String(ciphertextAndTag)}";
        var sig = Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(signer.PrivateKey, Encoding.UTF8.GetBytes(enc)));
        var certJson = JsonSerializer.Serialize(new { enc, sig, alg = "aes-256-gcm+ed25519+v2" });
        var file = MachineFile.Parse($"-----BEGIN MACHINE FILE-----\n{Convert.ToBase64String(Encoding.UTF8.GetBytes(certJson))}\n-----END MACHINE FILE-----");
        var keySet = SigningKeySet.FromPublicKeys(signer.PublicKeyB64);

        var ex = Assert.Throws<LicenseKeyMismatchException>(() => file.VerifyWithKeySet(
            LicenseScheme.Ed25519Sign, keySet, licenseKey, "fp-other-machine", ClockBeforeAnyExpiry));

        Assert.IsAssignableFrom<SignatureVerificationException>(ex);
        Assert.Equal(fingerprint, file.VerifyAndDecrypt(LicenseScheme.Ed25519Sign, keySet, licenseKey, fingerprint, ClockBeforeAnyExpiry).Fingerprint);
    }

    // ── Machine files ─────────────────────────────────────────────────────────

    [Fact]
    public void MachineFile_SignedBeforeARotation_StillVerifiesAgainstTheKeySet()
    {
        using var oldKey = NewSigner();
        using var newKey = NewSigner();

        const string fingerprint = "fp-rotate-1";
        var machineId = Guid.NewGuid();
        var file = MachineFile.Parse(BuildMachinePem(oldKey, MachinePayload(machineId, fingerprint, oldKey.Kid)));

        var keySet = SigningKeySet.FromResources(new[]
        {
            SigningKeyResourceFor(newKey.Kid, newKey.PublicKeyB64, "active"),
            SigningKeyResourceFor(oldKey.Kid, oldKey.PublicKeyB64, "retired"),
        });

        var (machine, claims, usedKey) = file.VerifyWithKeySet(
            LicenseScheme.Ed25519Sign, keySet, "unused-for-plain", fingerprint, ClockBeforeAnyExpiry);

        Assert.Equal(machineId, machine.Id);
        Assert.Equal(oldKey.Kid, claims.KeyId);
        Assert.Equal(oldKey.Kid, usedKey.KeyId);
    }

    [Fact]
    public void MachineFile_UnknownKid_IsDistinguishableFromAForgedSignature()
    {
        using var unknown = NewSigner();
        using var trusted = NewSigner();

        const string fingerprint = "fp-rotate-2";
        var file = MachineFile.Parse(BuildMachinePem(unknown, MachinePayload(Guid.NewGuid(), fingerprint, unknown.Kid)));
        var keySet = SigningKeySet.FromPublicKeys(trusted.PublicKeyB64);

        var ex = Assert.Throws<UnknownSigningKeyException>(() => file.VerifyWithKeySet(
            LicenseScheme.Ed25519Sign, keySet, "unused-for-plain", fingerprint, ClockBeforeAnyExpiry));

        Assert.Equal(unknown.Kid, ex.KeyId);
        Assert.IsNotType<SignatureVerificationException>(ex);
    }

    /// <summary>
    /// A server property, not a client limitation: for an RSA/ECDSA machine file the <c>kid</c>
    /// names the account's Ed25519 key while the signature was made with a different key entirely.
    /// Matching by kid there would report a genuine file as forged, so it is refused up front.
    /// </summary>
    [Theory]
    [InlineData(LicenseScheme.Rsa2048Pkcs1Sign)]
    [InlineData(LicenseScheme.Rsa2048Pkcs1PssSign)]
    [InlineData(LicenseScheme.EcdsaP256Sign)]
    public void MachineFile_KeySetPath_RefusesSchemesWhoseKidDoesNotNameTheSigningKey(LicenseScheme scheme)
    {
        using var signer = NewSigner();
        const string fingerprint = "fp-rotate-3";
        var file = MachineFile.Parse(BuildMachinePem(signer, MachinePayload(Guid.NewGuid(), fingerprint, signer.Kid)));

        var ex = Assert.Throws<SigningKeyNotApplicableException>(() => file.VerifyWithKeySet(
            scheme, SigningKeySet.FromPublicKeys(signer.PublicKeyB64), "unused", fingerprint, ClockBeforeAnyExpiry));

        Assert.Equal(scheme, ex.Scheme);
        Assert.IsAssignableFrom<SigningKeySelectionException>(ex);
    }

    [Fact]
    public void MachineFile_KeySetPath_StillRefusesJwtRs256()
    {
        using var signer = NewSigner();
        const string fingerprint = "fp-rotate-4";
        var file = MachineFile.Parse(BuildMachinePem(signer, MachinePayload(Guid.NewGuid(), fingerprint, signer.Kid)));

        Assert.Throws<SchemeNotSupportedException>(() => file.VerifyWithKeySet(
            LicenseScheme.Rsa2048JwtRs256, SigningKeySet.FromPublicKeys(signer.PublicKeyB64), "unused", fingerprint, ClockBeforeAnyExpiry));
    }

    /// <summary><see cref="LicenseScheme.None"/> signs with Ed25519, so it goes through unhindered.</summary>
    [Fact]
    public void MachineFile_KeySetPath_AcceptsTheNoneScheme()
    {
        using var signer = NewSigner();
        const string fingerprint = "fp-rotate-5";
        var file = MachineFile.Parse(BuildMachinePem(signer, MachinePayload(Guid.NewGuid(), fingerprint, signer.Kid)));

        var machine = file.VerifyAndDecrypt(
            LicenseScheme.None, SigningKeySet.FromPublicKeys(signer.PublicKeyB64), "unused", fingerprint, ClockBeforeAnyExpiry);

        Assert.Equal(fingerprint, machine.Fingerprint);
    }

    [Fact]
    public void MachineFile_KeySetPath_StillEnforcesTheSignedExpiry()
    {
        using var signer = NewSigner();
        const string fingerprint = "fp-rotate-6";
        var file = MachineFile.Parse(BuildMachinePem(signer, MachinePayload(Guid.NewGuid(), fingerprint, signer.Kid, exp: 1_000)));
        var keySet = SigningKeySet.FromPublicKeys(signer.PublicKeyB64);

        Assert.Throws<LicenseFileExpiredException>(() => file.VerifyWithKeySet(
            LicenseScheme.Ed25519Sign, keySet, "unused", fingerprint, 2_000));
    }

    [Fact]
    public void MachineFile_KeySetPath_RejectsANullKeySet()
    {
        using var signer = NewSigner();
        var file = MachineFile.Parse(BuildMachinePem(signer, MachinePayload(Guid.NewGuid(), "fp", signer.Kid)));

        Assert.Throws<ArgumentNullException>(() => file.VerifyWithKeySet(
            LicenseScheme.Ed25519Sign, null!, "unused", "fp", ClockBeforeAnyExpiry));
    }

    // ── Server-issued fixtures ────────────────────────────────────────────────

    /// <summary>
    /// The Ed25519 fixtures the SERVER issued verify through the key-set path, keyed by the kid the
    /// server itself wrote into them. This is the one case where the whole chain — server-written
    /// kid, locally computed key id, key selection, signature verify — is exercised against bytes
    /// this SDK did not produce.
    /// </summary>
    /// <remarks>
    /// ⚠ SCOPED TO Ed25519 DELIBERATELY — do not widen this to every fixture. The fixture manifest
    /// DISAGREES WITH THE SERVER on non-Ed25519 schemes: its generator called
    /// <c>encode_machine_file</c> directly and derived each <c>kid</c> from that file's own signing
    /// key, so the manifest carries four distinct kids (one per scheme). Production cannot produce
    /// that — <c>check_out_machine.rs:127-129</c> derives the <c>kid</c> from
    /// <c>account.ed25519_public_key</c> unconditionally, so one account emits ONE kid across every
    /// scheme. The fixtures pin the HASH RULE correctly and are used for exactly that in
    /// <c>SigningKeyIdTests</c>; asserting from them that a non-Ed25519 file's kid names its own
    /// signing key would bake a fixture-generator artifact into a test as if it were server
    /// behaviour.
    /// </remarks>
    [Fact]
    public void ServerIssuedEd25519Fixture_VerifiesThroughTheKeySetPath()
    {
        var manifest = LoadMachineFileManifest();
        var cases = manifest.Where(kv => kv.Value["scheme"]!.GetValue<string>() == "Ed25519Sign" &&
                                         !kv.Value["expired"]!.GetValue<bool>()).ToList();
        Assert.NotEmpty(cases);

        foreach (var (name, fixture) in cases)
        {
            var publicKeyB64 = fixture["public_key_b64"]!.GetValue<string>();
            var file = MachineFile.Parse(ReadFixture(fixture["file"]!.GetValue<string>()));

            // Built the way a caller pins a key: from the published key string alone.
            var keySet = SigningKeySet.FromPublicKeys(publicKeyB64);

            var (machine, claims, key) = file.VerifyWithKeySet(
                LicenseScheme.Ed25519Sign,
                keySet,
                fixture["license_key"]?.GetValue<string>() ?? string.Empty,
                fixture["fingerprint"]!.GetValue<string>(),
                ClockBeforeAnyExpiry);

            Assert.Equal(fixture["kid"]!.GetValue<string>(), claims.KeyId);
            Assert.Equal(claims.KeyId, key.KeyId);
            Assert.Equal(fixture["fingerprint"]!.GetValue<string>(), machine.Fingerprint);
            Assert.True(key.KeyIdIsSelfConsistent, $"{name}: server-issued kid disagreed with the locally computed one.");
        }
    }

    // ── Local-clock overloads and remaining edges ─────────────────────────────

    /// <summary>The local-clock overloads work on a file that carries no <c>exp</c> claim at all.</summary>
    [Fact]
    public void KeySetPath_LocalClockOverloads_VerifyAFileWithNoExpiry()
    {
        using var signer = NewSigner();
        var keySet = SigningKeySet.FromPublicKeys(signer.PublicKeyB64);

        var licenseFile = LicenseFile.Parse(BuildLicensePem(signer, LicensePayload(Guid.NewGuid(), signer.Kid)));
        Assert.Equal("LIC-ROTATE-1", licenseFile.VerifyAndDecrypt(keySet, "unused-for-plain").Key);

        const string fingerprint = "fp-local-clock";
        var machineFile = MachineFile.Parse(BuildMachinePem(signer, MachinePayload(Guid.NewGuid(), fingerprint, signer.Kid)));
        Assert.Equal(fingerprint, machineFile.VerifyAndDecrypt(LicenseScheme.Ed25519Sign, keySet, "unused", fingerprint).Fingerprint);
    }

    /// <summary>
    /// The <c>alg</c> gate is ahead of every entry point, the key-set one included: an encoding
    /// prefix that is neither <c>base64</c> nor <c>aes-256-gcm</c> is refused at Parse, not guessed at.
    /// </summary>
    [Fact]
    public void LicenseFile_AnUnrecognisedEncodingPrefix_IsRefusedAtParse_BeforeTheKeySetIsConsulted()
    {
        using var signer = NewSigner();
        var pem = BuildLicensePem(signer, LicensePayload(Guid.NewGuid(), signer.Kid), alg: "rot13+ed25519+v2");

        Assert.Throws<UnsupportedAlgorithmException>(() => LicenseFile.Parse(pem));
    }

    /// <summary>
    /// A machine file naming a key the caller DOES hold, signed by a different key, is a forgery —
    /// the machine-file mirror of the license-file case.
    /// </summary>
    [Fact]
    public void MachineFile_ForgedSignatureUnderATrustedKid_IsASignatureFailure()
    {
        using var forger = NewSigner();
        using var trusted = NewSigner();

        const string fingerprint = "fp-forged";
        var file = MachineFile.Parse(BuildMachinePem(forger, MachinePayload(Guid.NewGuid(), fingerprint, trusted.Kid)));

        var ex = Assert.Throws<SignatureVerificationException>(() => file.VerifyWithKeySet(
            LicenseScheme.Ed25519Sign, SigningKeySet.FromPublicKeys(trusted.PublicKeyB64), "unused", fingerprint, ClockBeforeAnyExpiry));

        Assert.Contains("forged or corrupted", ex.Message, StringComparison.Ordinal);
        Assert.IsNotAssignableFrom<SigningKeySelectionException>(ex);
    }

    /// <summary>The failure carries what the set DID hold, so a log line can show both sides.</summary>
    [Fact]
    public void UnknownSigningKey_CarriesTheAvailableKeyIds()
    {
        using var signer = NewSigner();
        using var trusted = NewSigner();

        var file = LicenseFile.Parse(BuildLicensePem(signer, LicensePayload(Guid.NewGuid(), signer.Kid)));
        var ex = Assert.Throws<UnknownSigningKeyException>(() => file.VerifyWithKeySet(
            SigningKeySet.FromPublicKeys(trusted.PublicKeyB64), "unused", ClockBeforeAnyExpiry));

        Assert.Equal(new[] { trusted.Kid }, ex.AvailableKeyIds);
        Assert.Contains(trusted.Kid, ex.Message, StringComparison.Ordinal);

        // An empty set says so rather than printing an empty list.
        var emptyEx = Assert.Throws<UnknownSigningKeyException>(() => file.VerifyWithKeySet(
            SigningKeySet.Empty, "unused", ClockBeforeAnyExpiry));
        Assert.Empty(emptyEx.AvailableKeyIds);
        Assert.Contains("no usable key", emptyEx.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Additive-only contract ────────────────────────────────────────────────

    /// <summary>
    /// This change is additive: every pre-existing verification entry point must still exist with
    /// exactly its old signature. The key-set overloads are new surface beside them, never a
    /// replacement — this ships as a PATCH, so a removed or altered signature would be a silent
    /// breaking change for consumers on <c>^2.0</c>.
    /// </summary>
    [Fact]
    public void TheSingleKeyEntryPoints_AreUnchanged()
    {
        Assert.NotNull(typeof(LicenseFile).GetMethod(
            nameof(LicenseFile.Verify), new[] { typeof(ReadOnlySpan<byte>) }));
        Assert.NotNull(typeof(LicenseFile).GetMethod(
            nameof(LicenseFile.VerifyAndDecrypt), new[] { typeof(ReadOnlySpan<byte>), typeof(string) }));
        Assert.NotNull(typeof(LicenseFile).GetMethod(
            nameof(LicenseFile.VerifyAndDecrypt), new[] { typeof(ReadOnlySpan<byte>), typeof(string), typeof(long) }));
        Assert.NotNull(typeof(LicenseFile).GetMethod(
            nameof(LicenseFile.VerifyWithClaims), new[] { typeof(ReadOnlySpan<byte>), typeof(string), typeof(long) }));

        Assert.NotNull(typeof(MachineFile).GetMethod(
            nameof(MachineFile.Verify), new[] { typeof(LicenseScheme), typeof(ReadOnlySpan<byte>) }));
        Assert.NotNull(typeof(MachineFile).GetMethod(
            nameof(MachineFile.VerifyAndDecrypt), new[] { typeof(LicenseScheme), typeof(ReadOnlySpan<byte>), typeof(string), typeof(string) }));
        Assert.NotNull(typeof(MachineFile).GetMethod(
            nameof(MachineFile.VerifyAndDecrypt), new[] { typeof(LicenseScheme), typeof(ReadOnlySpan<byte>), typeof(string), typeof(string), typeof(long) }));
        Assert.NotNull(typeof(MachineFile).GetMethod(
            nameof(MachineFile.VerifyWithClaims), new[] { typeof(LicenseScheme), typeof(ReadOnlySpan<byte>), typeof(string), typeof(string), typeof(long) }));
    }

    /// <summary>
    /// The refactor that let both paths share one payload pipeline must not have changed what the
    /// single-key path does: same order of checks, same exception for the same input.
    /// </summary>
    [Fact]
    public void TheSingleKeyPath_StillBehavesExactlyAsBefore()
    {
        using var signer = NewSigner();
        using var other = NewSigner();

        var pem = BuildLicensePem(signer, LicensePayload(Guid.NewGuid(), signer.Kid));
        var file = LicenseFile.Parse(pem);

        Assert.True(file.Verify(signer.PublicKey));
        Assert.False(file.Verify(other.PublicKey));
        Assert.Equal("LIC-ROTATE-1", file.VerifyAndDecrypt(signer.PublicKey, "unused", ClockBeforeAnyExpiry).Key);
        Assert.Throws<SignatureVerificationException>(() => file.VerifyAndDecrypt(other.PublicKey, "unused", ClockBeforeAnyExpiry));

        // A pre-v2 file is refused before anything else — at Parse now, so no verifier and no key
        // is ever involved. A v1 alg with a perfectly good signature is UnsupportedAlgorithm, not
        // a signature failure, on every entry point.
        var v1 = BuildLicensePem(signer, LicensePayload(Guid.NewGuid(), signer.Kid), alg: "base64+ed25519");
        Assert.Throws<UnsupportedAlgorithmException>(() => LicenseFile.Parse(v1));
    }

    /// <summary>
    /// The non-Ed25519 server-issued fixtures are refused by the key-set path, by scheme, before
    /// any kid is looked at.
    /// </summary>
    /// <remarks>
    /// This is the behaviour that keeps the manifest's per-scheme kids from ever being trusted as
    /// a key selector. In production those files carry the account's Ed25519 kid, which names a
    /// real published key that cannot verify an RSA/ECDSA signature — a naive kid lookup would
    /// report an authentic file as forged, reintroducing the very defect this change closes.
    /// </remarks>
    [Fact]
    public void ServerIssuedNonEd25519Fixtures_AreRefusedByTheKeySetPath()
    {
        var manifest = LoadMachineFileManifest();
        var cases = manifest.Where(kv => kv.Value["scheme"]!.GetValue<string>() != "Ed25519Sign").ToList();
        Assert.NotEmpty(cases);

        foreach (var (name, fixture) in cases)
        {
            var file = MachineFile.Parse(ReadFixture(fixture["file"]!.GetValue<string>()));
            var scheme = Enum.Parse<LicenseScheme>(fixture["scheme"]!.GetValue<string>());
            var keySet = SigningKeySet.FromPublicKeys("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");

            var ex = Assert.Throws<SigningKeyNotApplicableException>(() => file.VerifyWithKeySet(
                scheme, keySet, fixture["license_key"]?.GetValue<string>() ?? string.Empty,
                fixture["fingerprint"]!.GetValue<string>(), ClockBeforeAnyExpiry));

            Assert.Equal(scheme, ex.Scheme);

            // ...and they still verify perfectly well through the scheme-taking path, which is
            // where they belong. Nothing is lost by refusing them above.
            var publicKey = Convert.FromBase64String(fixture["public_key_b64"]!.GetValue<string>());
            Assert.True(file.Verify(scheme, publicKey), $"{name}: should still verify with its own scheme key.");
        }
    }

    private static SigningKeyResource SigningKeyResourceFor(string kid, string publicKey, string status) =>
        JsonSerializer.Deserialize<SigningKeyResource>(
            $$"""
              {
                "type": "signing-keys",
                "id": "{{kid}}",
                "attributes": { "algorithm": "ed25519", "publicKey": "{{publicKey}}", "status": "{{status}}", "created": "2026-01-01T00:00:00Z" }
              }
              """,
            TamgaJsonOptions.Default)!;

    private static string ReadFixture(string fileName)
    {
        using var stream = typeof(SigningKeyRotationTests).GetTypeInfo().Assembly
            .GetManifestResourceStream($"MachineFileFixtures/{fileName}")
            ?? throw new InvalidOperationException($"Embedded fixture '{fileName}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static IReadOnlyDictionary<string, JsonObject> LoadMachineFileManifest()
    {
        using var stream = typeof(SigningKeyRotationTests).GetTypeInfo().Assembly
            .GetManifestResourceStream("MachineFileFixtures/manifest.json")
            ?? throw new InvalidOperationException("Embedded machine-file manifest is missing.");
        return JsonNode.Parse(stream)!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.AsObject(), StringComparer.Ordinal);
    }
}
