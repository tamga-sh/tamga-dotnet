using System.Security.Cryptography;
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
/// Locally-constructed round-trip coverage for <see cref="MachineFile"/>'s edge cases — TTL
/// validation, PEM-envelope hardening, scheme refusal, and the parts of the pipeline that need a
/// deliberately broken input.
/// </summary>
/// <remarks>
/// These files are built here, so they prove only that this SDK is self-consistent. The authority
/// on the wire format is <see cref="MachineFileFixtureTests"/>, which runs against files the
/// SERVER produced. Anything asserted here about the format has to agree with those fixtures — if
/// the two ever disagree, the fixtures are right. Keep the shapes below (the mandatory
/// <c>+v2</c> alg suffix, the signed <c>meta</c> claims, the dot-separated encrypted <c>enc</c>)
/// in sync with them.
/// </remarks>
public class MachineFileTests
{
    /// <summary>A fixed issue time, so nothing here depends on the wall clock.</summary>
    private const long IssuedAt = 1_700_000_000;

    /// <summary>One hour of validity from <see cref="IssuedAt"/>.</summary>
    private const long ExpiresAt = IssuedAt + 3600;

    private static string BuildPayloadJson(Guid machineId, string fingerprint) => new JsonObject
    {
        ["data"] = new JsonObject
        {
            ["type"] = "machines",
            ["id"] = machineId.ToString(),
            ["attributes"] = new JsonObject
            {
                ["fingerprint"] = fingerprint,
                ["name"] = "test-machine",
            },
        },
        // Format v2: the claims live INSIDE the signed bytes. A payload without them is a pre-v2
        // file and must be refused, so every fixture built here carries them.
        ["meta"] = new JsonObject
        {
            ["iat"] = IssuedAt,
            ["exp"] = ExpiresAt,
            ["jti"] = "01936f2a-0000-7000-8000-0000000000ff",
            ["kid"] = "0123456789abcdef",
        },
    }.ToJsonString();

    /// <summary>
    /// Builds an encrypted <c>enc</c> in the server's real shape:
    /// <c>base64(nonce) + "." + base64(ciphertext || tag)</c>, two independently-encoded halves.
    /// </summary>
    private static string BuildEncryptedEnc(byte[] plaintext, byte[] aesKey, byte[] nonce)
    {
        var (ciphertext, tag) = AesGcmCipher.Seal(aesKey, nonce, plaintext);
        var ciphertextAndTag = new byte[ciphertext.Length + tag.Length];
        Buffer.BlockCopy(ciphertext, 0, ciphertextAndTag, 0, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, ciphertextAndTag, ciphertext.Length, tag.Length);
        return $"{Convert.ToBase64String(nonce)}.{Convert.ToBase64String(ciphertextAndTag)}";
    }

    private static string WrapPem(string certJson)
    {
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(certJson));
        return $"-----BEGIN MACHINE FILE-----\n{body}\n-----END MACHINE FILE-----";
    }

    private static string BuildPem(string enc, byte[] signature, string alg)
    {
        var certJson = JsonSerializer.Serialize(new { enc, sig = Convert.ToBase64String(signature), alg });
        return WrapPem(certJson);
    }

    /// <summary>
    /// Builds a machine file whose signature is genuinely valid over an arbitrary <c>enc</c>
    /// string. Everything past the signature check — encoding split, base64 decode, payload parse,
    /// expiry — only runs on a file that already verified, so reaching those branches needs a
    /// correctly-signed file carrying a deliberately broken payload, not a corrupt one.
    /// </summary>
    private static (MachineFile File, byte[] PublicKey) SignedFileWithEnc(string enc, string alg)
    {
        var (publicKeyBytes, sign, _) = MakeSigner(LicenseScheme.Ed25519Sign);
        var signature = sign(Encoding.UTF8.GetBytes(enc));
        return (MachineFile.Parse(BuildPem(enc, signature, alg)), publicKeyBytes);
    }

    private static MachineFile ParseSignedPlain(string encPlaintextOrRaw, bool alreadyBase64, out byte[] publicKey)
    {
        var enc = alreadyBase64 ? encPlaintextOrRaw : Convert.ToBase64String(Encoding.UTF8.GetBytes(encPlaintextOrRaw));
        var (file, key) = SignedFileWithEnc(enc, "base64+ed25519+v2");
        publicKey = key;
        return file;
    }

    // ── Payload parsing, on files that DID verify ────────────────────────────

    [Fact]
    public void VerifyAndDecrypt_RejectsAPayloadThatIsNotJson()
    {
        var file = ParseSignedPlain("this is not json at all {", alreadyBase64: false, out var publicKey);

        var ex = Assert.Throws<OfflineFileFormatException>(() =>
            file.VerifyAndDecrypt(LicenseScheme.Ed25519Sign, publicKey, "k", "fp", IssuedAt));
        Assert.Contains("payload JSON is malformed", ex.Message);
    }

    /// <summary>
    /// A literal <c>null</c> is valid JSON that deserializes to <see langword="null"/> — so it
    /// passes the parse and has to be caught by the explicit null check rather than by the
    /// <c>JsonException</c> handler above.
    /// </summary>
    [Fact]
    public void VerifyAndDecrypt_RejectsALiteralNullPayload()
    {
        var file = ParseSignedPlain("null", alreadyBase64: false, out var publicKey);

        var ex = Assert.Throws<OfflineFileFormatException>(() =>
            file.VerifyAndDecrypt(LicenseScheme.Ed25519Sign, publicKey, "k", "fp", IssuedAt));
        Assert.Contains("payload was empty", ex.Message);
    }

    /// <summary>
    /// The second line behind the <c>+v2</c> alg gate. A file whose alg claims v2 but whose signed
    /// payload carries no <c>meta</c> would otherwise reach the expiry check with nothing to check
    /// — i.e. it would never expire, which is the exact defect format v2 exists to close.
    /// </summary>
    [Fact]
    public void VerifyAndDecrypt_RejectsAV2FileWhosePayloadHasNoSignedClaims()
    {
        var payload = new JsonObject
        {
            ["data"] = new JsonObject { ["type"] = "machines", ["id"] = Guid.NewGuid().ToString() },
        }.ToJsonString();
        var file = ParseSignedPlain(payload, alreadyBase64: false, out var publicKey);

        var ex = Assert.Throws<OfflineFileFormatException>(() =>
            file.VerifyAndDecrypt(LicenseScheme.Ed25519Sign, publicKey, "k", "fp", IssuedAt));
        Assert.Contains("missing the signed 'meta' claims", ex.Message);
        Assert.Contains("pre-v2", ex.Message);
    }

    [Fact]
    public void VerifyAndDecrypt_RejectsAPlainEncThatIsNotBase64()
    {
        var file = ParseSignedPlain("!!! definitely not base64 !!!", alreadyBase64: true, out var publicKey);

        var ex = Assert.Throws<OfflineFileFormatException>(() =>
            file.VerifyAndDecrypt(LicenseScheme.Ed25519Sign, publicKey, "k", "fp", IssuedAt));
        Assert.Contains("'enc' is not valid base64", ex.Message);
    }

    // ── The dot-separated encrypted `enc` ────────────────────────────────────

    private const string EncryptedAlg = "aes-256-gcm+ed25519+v2";

    [Fact]
    public void VerifyAndDecrypt_RejectsAnEncryptedEncWithNoSeparator()
    {
        var (file, publicKey) = SignedFileWithEnc(Convert.ToBase64String(new byte[32]), EncryptedAlg);

        var ex = Assert.Throws<OfflineFileFormatException>(() =>
            file.VerifyAndDecrypt(LicenseScheme.Ed25519Sign, publicKey, "k", "fp", IssuedAt));
        Assert.Contains("missing its '.' separator", ex.Message);
    }

    /// <summary>
    /// Standard base64 has no <c>.</c>, so a second separator cannot have come from the server's
    /// encoder — splitting on the first one and hoping would silently decode a truncated
    /// ciphertext.
    /// </summary>
    [Fact]
    public void VerifyAndDecrypt_RejectsAnEncryptedEncWithTwoSeparators()
    {
        var half = Convert.ToBase64String(new byte[12]);
        var (file, publicKey) = SignedFileWithEnc($"{half}.{half}.{half}", EncryptedAlg);

        var ex = Assert.Throws<OfflineFileFormatException>(() =>
            file.VerifyAndDecrypt(LicenseScheme.Ed25519Sign, publicKey, "k", "fp", IssuedAt));
        Assert.Contains("more than one '.' separator", ex.Message);
    }

    [Theory]
    [InlineData("!!!not base64!!!", "nonce")]
    public void VerifyAndDecrypt_RejectsAnEncryptedHalfThatIsNotBase64(string badHalf, string which)
    {
        var good = Convert.ToBase64String(new byte[32]);
        var (file, publicKey) = SignedFileWithEnc($"{badHalf}.{good}", EncryptedAlg);

        var ex = Assert.Throws<OfflineFileFormatException>(() =>
            file.VerifyAndDecrypt(LicenseScheme.Ed25519Sign, publicKey, "k", "fp", IssuedAt));
        Assert.Contains($"{which} is not valid base64", ex.Message);
    }

    [Fact]
    public void VerifyAndDecrypt_RejectsAWrongLengthNonce()
    {
        var shortNonce = Convert.ToBase64String(new byte[8]);
        var ciphertext = Convert.ToBase64String(new byte[32]);
        var (file, publicKey) = SignedFileWithEnc($"{shortNonce}.{ciphertext}", EncryptedAlg);

        var ex = Assert.Throws<OfflineFileFormatException>(() =>
            file.VerifyAndDecrypt(LicenseScheme.Ed25519Sign, publicKey, "k", "fp", IssuedAt));
        Assert.Contains("nonce is 8 bytes", ex.Message);
    }

    /// <summary>
    /// The ciphertext half is <c>ciphertext || tag</c>, so anything shorter than the 16-byte GCM
    /// tag cannot be split into the two spans the opener needs — caught here rather than as an
    /// out-of-range slice deeper in.
    /// </summary>
    [Fact]
    public void VerifyAndDecrypt_RejectsACiphertextShorterThanTheGcmTag()
    {
        var nonce = Convert.ToBase64String(new byte[AesGcmCipher.NonceLength]);
        var tooShort = Convert.ToBase64String(new byte[4]);
        var (file, publicKey) = SignedFileWithEnc($"{nonce}.{tooShort}", EncryptedAlg);

        var ex = Assert.Throws<OfflineFileFormatException>(() =>
            file.VerifyAndDecrypt(LicenseScheme.Ed25519Sign, publicKey, "k", "fp", IssuedAt));
        Assert.Contains("expected at least the 16-byte GCM tag", ex.Message);
    }

    // ── Verifier dispatch with an unusable public key ────────────────────────
    //
    // TryImportPublicKey returns null for bytes that are not a key in any accepted encoding, and
    // both verifier wrappers must then fail CLOSED. Short-circuiting to `false` is the whole point:
    // a null-dereference or a thrown import error would be an unverified file taking a different
    // code path from a forged one.

    [Theory]
    [InlineData(LicenseScheme.Rsa2048Pkcs1Sign)]
    [InlineData(LicenseScheme.Rsa2048Pkcs1PssSign)]
    [InlineData(LicenseScheme.EcdsaP256Sign)]
    public void Verify_FailsClosed_WhenThePublicKeyCannotBeImported(LicenseScheme scheme)
    {
        var payloadJson = BuildPayloadJson(Guid.NewGuid(), "fp-abc");
        var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson));
        var (_, sign, alg) = MakeSigner(scheme);
        var signature = sign(Encoding.UTF8.GetBytes(enc));
        var file = MachineFile.Parse(BuildPem(enc, signature, alg));

        var garbage = new byte[64];
        RandomNumberGenerator.Fill(garbage);

        Assert.False(file.Verify(scheme, garbage));
        Assert.Throws<SignatureVerificationException>(() =>
            file.VerifyAndDecrypt(scheme, garbage, "k", "fp", IssuedAt));
    }

    /// <summary>
    /// The end-to-end form of the off-curve case, and the assertion that actually matters: a
    /// 65-byte <c>0x04</c> point whose coordinates are not on P-256 has the right SHAPE, so it
    /// reaches the raw-point import — and the whole call must come back as a failed verification
    /// on every platform. On Windows/CNG the invalid parameters surface as
    /// <see cref="PlatformNotSupportedException"/> rather than a
    /// <see cref="System.Security.Cryptography.CryptographicException"/>, which used to escape
    /// here while Linux and macOS failed closed.
    /// </summary>
    [Fact]
    public void Verify_FailsClosed_OnAnEcdsaKeyWhosePointIsNotOnTheCurve()
    {
        var payloadJson = BuildPayloadJson(Guid.NewGuid(), "fp-abc");
        var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson));
        var (_, sign, alg) = MakeSigner(LicenseScheme.EcdsaP256Sign);
        var signature = sign(Encoding.UTF8.GetBytes(enc));
        var file = MachineFile.Parse(BuildPem(enc, signature, alg));

        var notOnCurve = new byte[65];
        notOnCurve[0] = 0x04;
        for (var i = 1; i < notOnCurve.Length; i++)
        {
            notOnCurve[i] = 0x01;
        }

        Assert.False(file.Verify(LicenseScheme.EcdsaP256Sign, notOnCurve));
        Assert.Throws<SignatureVerificationException>(() =>
            file.VerifyAndDecrypt(LicenseScheme.EcdsaP256Sign, notOnCurve, "k", "fp", IssuedAt));
    }

    public static IEnumerable<object[]> AllSchemes()
    {
        yield return new object[] { LicenseScheme.Ed25519Sign };
        yield return new object[] { LicenseScheme.Rsa2048Pkcs1Sign };
        yield return new object[] { LicenseScheme.Rsa2048Pkcs1PssSign };
        yield return new object[] { LicenseScheme.EcdsaP256Sign };
    }

    [Theory]
    [MemberData(nameof(AllSchemes))]
    public void Parse_VerifyAndDecrypt_RoundTrips_PlainMachineFile_ForEveryScheme(LicenseScheme scheme)
    {
        var machineId = Guid.NewGuid();
        var payloadJson = BuildPayloadJson(machineId, "fp-plain-abc");
        var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson));
        var (publicKeyBytes, sign, alg) = MakeSigner(scheme);
        var signature = sign(Encoding.UTF8.GetBytes(enc));

        var pem = BuildPem(enc, signature, alg);
        var machineFile = MachineFile.Parse(pem);

        Assert.True(machineFile.Verify(scheme, publicKeyBytes));
        var machine = machineFile.VerifyAndDecrypt(scheme, publicKeyBytes, licenseKey: "unused", fingerprint: "unused", nowUnixSeconds: IssuedAt);
        Assert.Equal(machineId, machine.Id);
        Assert.Equal("fp-plain-abc", machine.Fingerprint);
    }

    [Theory]
    [MemberData(nameof(AllSchemes))]
    public void Verify_FailsClosed_OnTamperedSignature_ForEveryScheme(LicenseScheme scheme)
    {
        var payloadJson = BuildPayloadJson(Guid.NewGuid(), "fp-abc");
        var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson));
        var (publicKeyBytes, sign, alg) = MakeSigner(scheme);
        var signature = sign(Encoding.UTF8.GetBytes(enc));
        signature[0] ^= 0xFF;

        var pem = BuildPem(enc, signature, alg);
        var machineFile = MachineFile.Parse(pem);
        Assert.False(machineFile.Verify(scheme, publicKeyBytes));
    }

    [Fact]
    public void VerifyAndDecrypt_RoundTrips_EncryptedMachineFile_ViaHkdf()
    {
        var machineId = Guid.NewGuid();
        const string fingerprint = "fp-encrypted-abc";
        const string licenseKey = "the-license-key";
        var payloadJson = BuildPayloadJson(machineId, fingerprint);
        var plaintext = Encoding.UTF8.GetBytes(payloadJson);

        var nonce = new byte[AesGcmCipher.NonceLength];
        RandomNumberGenerator.Fill(nonce);
        var aesKey = Hkdf.DeriveMachineFileKey(licenseKey, fingerprint);
        var enc = BuildEncryptedEnc(plaintext, aesKey, nonce);

        var (publicKeyBytes, sign, _) = MakeSigner(LicenseScheme.Ed25519Sign);
        var signature = sign(Encoding.UTF8.GetBytes(enc));
        var pem = BuildPem(enc, signature, "aes-256-gcm+ed25519+v2");

        var machineFile = MachineFile.Parse(pem);
        var machine = machineFile.VerifyAndDecrypt(LicenseScheme.Ed25519Sign, publicKeyBytes, licenseKey, fingerprint, IssuedAt);
        Assert.Equal(machineId, machine.Id);
    }

    [Fact]
    public void VerifyAndDecrypt_FailsClosed_WithWrongFingerprint()
    {
        // CRITICAL: wrong HKDF `info` (fingerprint) must derive a different key and fail AES-GCM
        // authentication — never silently decrypt garbage.
        var machineId = Guid.NewGuid();
        const string realFingerprint = "fp-real";
        const string licenseKey = "the-license-key";
        var payloadJson = BuildPayloadJson(machineId, realFingerprint);
        var plaintext = Encoding.UTF8.GetBytes(payloadJson);

        var nonce = new byte[AesGcmCipher.NonceLength];
        var aesKey = Hkdf.DeriveMachineFileKey(licenseKey, realFingerprint);
        var enc = BuildEncryptedEnc(plaintext, aesKey, nonce);

        var (publicKeyBytes, sign, _) = MakeSigner(LicenseScheme.Ed25519Sign);
        var signature = sign(Encoding.UTF8.GetBytes(enc));
        var pem = BuildPem(enc, signature, "aes-256-gcm+ed25519+v2");

        var machineFile = MachineFile.Parse(pem);
        // Wrong HKDF `info` derives a different key and fails AES-GCM authentication after a
        // verified signature — that is the wrong-key-material verdict, never silent garbage.
        var ex = Assert.Throws<LicenseKeyMismatchException>(() =>
            machineFile.VerifyAndDecrypt(LicenseScheme.Ed25519Sign, publicKeyBytes, licenseKey, "fp-wrong", IssuedAt));
        Assert.IsAssignableFrom<SignatureVerificationException>(ex);
    }

    [Fact]
    public void Verify_Throws_SchemeNotSupported_ForRsaJwtRs256()
    {
        var payloadJson = BuildPayloadJson(Guid.NewGuid(), "fp-abc");
        var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson));
        // A well-formed v2 alg with the suffix the server emits for BOTH RSA schemes. Parse's
        // format gate passes; the scheme refusal is the first thing the verifying path does.
        var pem = BuildPem(enc, new byte[64], "base64+rsa-sha256+v2");
        var machineFile = MachineFile.Parse(pem);

        Assert.Throws<SchemeNotSupportedException>(() => machineFile.Verify(LicenseScheme.Rsa2048JwtRs256, new byte[32]));
    }

    [Fact]
    public void VerifyAndDecrypt_Throws_SchemeNotSupported_ForRsaJwtRs256_BeforeAttemptingSignatureOrDecryption()
    {
        var payloadJson = BuildPayloadJson(Guid.NewGuid(), "fp-abc");
        var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson));
        // Encrypted alg over a plain (non dot-separated) enc with a junk signature: neither the
        // signature check nor the decrypt may run before the scheme refusal.
        var pem = BuildPem(enc, new byte[64], "aes-256-gcm+rsa-sha256+v2");
        var machineFile = MachineFile.Parse(pem);

        Assert.Throws<SchemeNotSupportedException>(() =>
            machineFile.VerifyAndDecrypt(LicenseScheme.Rsa2048JwtRs256, new byte[32], "key", "fp"));
    }

    /// <summary>
    /// D17 for machine files: the grammar, the +v2 marker and the encoding prefix are checked at
    /// Parse, before any scheme or key is known. The signing-suffix cross-check needs the caller's
    /// scheme and stays on the verifying path.
    /// </summary>
    [Theory]
    [InlineData("base64+ed25519")]
    [InlineData("aes-256-gcm+rsa-sha256")]
    [InlineData("rsa-sha256")]
    [InlineData("base64+ed25519+v3")]
    [InlineData("base64+ed25519+V2")]
    [InlineData("base64+ed25519+v2junk")]
    [InlineData("xbase64+ed25519+v2")]
    [InlineData("base64-ed25519-v2")]
    [InlineData("base64")]
    [InlineData("")]
    public void Parse_RefusesAnAlgOutsideTheV2Grammar_BeforeAnyKeyOrSignatureWork(string alg)
    {
        var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(BuildPayloadJson(Guid.NewGuid(), "fp-abc")));
        var (_, sign, _) = MakeSigner(LicenseScheme.Ed25519Sign);

        Assert.Throws<UnsupportedAlgorithmException>(() => MachineFile.Parse(BuildPem(enc, sign(Encoding.UTF8.GetBytes(enc)), alg)));
    }

    [Fact]
    public void Parse_LeavesTheSigningSuffixCrossCheck_ToTheVerifyingPath()
    {
        // Grammar-valid, but the suffix contradicts the scheme the caller will pass. Parse has no
        // scheme, so it cannot judge this; VerifyAndDecrypt does, after the signature.
        var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(BuildPayloadJson(Guid.NewGuid(), "fp-abc")));
        var (publicKey, sign, _) = MakeSigner(LicenseScheme.Ed25519Sign);
        var file = MachineFile.Parse(BuildPem(enc, sign(Encoding.UTF8.GetBytes(enc)), "base64+ecdsa-p256+v2"));

        Assert.Throws<UnsupportedAlgorithmException>(() =>
            file.VerifyAndDecrypt(LicenseScheme.Ed25519Sign, publicKey, "k", "fp", IssuedAt));
    }

    [Fact]
    public void Parse_Throws_OfflineFileFormatException_NotArgumentOutOfRangeException_OnOverlappingMarkers()
    {
        // Security-review regression (see LicenseFileTests' equivalent) — same shared
        // PemEnvelope.Strip logic, same overlap hazard for the MACHINE FILE markers.
        const string beginMarker = "-----BEGIN MACHINE FILE-----";
        const string endMarker = "-----END MACHINE FILE-----";
        var overlapping = beginMarker + endMarker[1..];

        Assert.Throws<OfflineFileFormatException>(() => MachineFile.Parse(overlapping));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(31536001)]
    public void ValidateTtl_Throws_ForOutOfRangeValues(int ttl)
    {
        Assert.Throws<TtlInvalidException>(() => MachineFile.ValidateTtl(ttl));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(31536000)]
    [InlineData(86400)]
    public void ValidateTtl_DoesNotThrow_ForValidValues(int ttl)
    {
        MachineFile.ValidateTtl(ttl);
    }

    /// <summary>Builds a (publicKeyBytes, signFn, algSuffix) tuple for the given scheme, using freshly-generated test keys.</summary>
    private static (byte[] PublicKey, Func<byte[], byte[]> Sign, string Alg) MakeSigner(LicenseScheme scheme)
    {
        switch (scheme)
        {
            case LicenseScheme.Ed25519Sign:
                {
                    var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
                    var publicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
                    return (publicKey, msg => SignatureAlgorithm.Ed25519.Sign(key, msg), "base64+ed25519+v2");
                }

            case LicenseScheme.Rsa2048Pkcs1Sign:
                {
                    var rsa = RSA.Create(2048);
                    var publicKey = rsa.ExportSubjectPublicKeyInfo();
                    return (publicKey, msg => rsa.SignData(msg, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), "base64+rsa-sha256+v2");
                }

            case LicenseScheme.Rsa2048Pkcs1PssSign:
                {
                    var rsa = RSA.Create(2048);
                    var publicKey = rsa.ExportSubjectPublicKeyInfo();
                    return (publicKey, msg => rsa.SignData(msg, HashAlgorithmName.SHA256, RSASignaturePadding.Pss), "base64+rsa-pss-sha256+v2");
                }

            case LicenseScheme.EcdsaP256Sign:
                {
                    var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                    var publicKey = ecdsa.ExportSubjectPublicKeyInfo();
                    // DER, matching the server's ECDSA_P256_SHA256_ASN1_SIGNING — a fixture
                    // signed as raw P1363 would test the SDK against itself, not the server.
                    return (publicKey, msg => ecdsa.SignData(msg, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence), "base64+ecdsa-p256+v2");
                }

            default:
                throw new NotSupportedException($"No test signer for {scheme}.");
        }
    }
}
