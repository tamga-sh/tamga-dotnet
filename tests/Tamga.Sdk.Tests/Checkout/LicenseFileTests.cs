using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NSec.Cryptography;
using Tamga.Sdk.Checkout;
using Tamga.Sdk.Crypto;
using Xunit;

namespace Tamga.Sdk.Tests.Checkout;

public class LicenseFileTests
{
    private static (byte[] PublicKey, Key PrivateKey) GenerateKeyPair()
    {
        var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return (key.PublicKey.Export(KeyBlobFormat.RawPublicKey), key);
    }

    private static string BuildPayloadJson(Guid licenseId, string key, long? exp = null)
    {
        var meta = new JsonObject
        {
            ["iat"] = 1767225600L,
            ["jti"] = "test-jti",
            ["kid"] = "test-kid",
        };
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
                ["attributes"] = new JsonObject
                {
                    ["key"] = key,
                    ["suspended"] = false,
                    ["uses"] = 3,
                },
            },
            // Format v2 puts the claims inside the signed bytes. A payload
            // without them is a v1 file and no longer verifies.
            ["meta"] = meta,
        }.ToJsonString();
    }

    private static string WrapPem(string certJson)
    {
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(certJson));
        return $"-----BEGIN LICENSE FILE-----\n{body}\n-----END LICENSE FILE-----";
    }

    /// <summary>Builds a syntactically/cryptographically valid .lic PEM exactly as the server would, for round-trip testing.</summary>
    private static string BuildValidPem(Key privateKey, string enc, string alg)
    {
        var sig = Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(privateKey, Encoding.UTF8.GetBytes(enc)));
        var certJson = JsonSerializer.Serialize(new { enc, sig, alg });
        return WrapPem(certJson);
    }

    [Fact]
    public void Parse_VerifyAndDecrypt_RoundTrips_PlainLicenseFile()
    {
        var (publicKey, privateKey) = GenerateKeyPair();
        using var _ = privateKey;
        var licenseId = Guid.NewGuid();
        var payloadJson = BuildPayloadJson(licenseId, "LIC-ABC-123");
        var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson));
        var pem = BuildValidPem(privateKey, enc, "base64+ed25519+v2");

        var licenseFile = LicenseFile.Parse(pem);
        Assert.True(licenseFile.Verify(publicKey));

        var license = licenseFile.VerifyAndDecrypt(publicKey, licenseKey: "unused-for-plain-files");
        Assert.Equal(licenseId, license.Id);
        Assert.Equal("LIC-ABC-123", license.Key);
        Assert.Equal(3, license.Uses);
    }

    [Fact]
    public void Parse_VerifyAndDecrypt_RoundTrips_EncryptedLicenseFile()
    {
        var (publicKey, privateKey) = GenerateKeyPair();
        using var _ = privateKey;
        var licenseId = Guid.NewGuid();
        const string licenseKey = "super-secret-license-key";
        var payloadJson = BuildPayloadJson(licenseId, licenseKey);
        var plaintext = Encoding.UTF8.GetBytes(payloadJson);

        var nonce = new byte[AesGcmCipher.NonceLength];
        System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);
        var aesKey = Hkdf.DeriveLicenseFileKey(licenseKey);
        var (ciphertext, tag) = AesGcmCipher.Seal(aesKey, nonce, plaintext);
        var payload = new byte[nonce.Length + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, payload, nonce.Length, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length + ciphertext.Length, tag.Length);
        var enc = Convert.ToBase64String(payload);

        var pem = BuildValidPem(privateKey, enc, "aes-256-gcm+ed25519+v2");
        var licenseFile = LicenseFile.Parse(pem);
        Assert.True(licenseFile.Verify(publicKey));

        var license = licenseFile.VerifyAndDecrypt(publicKey, licenseKey);
        Assert.Equal(licenseId, license.Id);
        Assert.Equal(licenseKey, license.Key);
    }

    [Fact]
    public void Verify_FailsClosed_OnTamperedSignature()
    {
        var (publicKey, privateKey) = GenerateKeyPair();
        using var _ = privateKey;
        var payloadJson = BuildPayloadJson(Guid.NewGuid(), "LIC-ABC");
        var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson));
        var sigBytes = SignatureAlgorithm.Ed25519.Sign(privateKey, Encoding.UTF8.GetBytes(enc));
        sigBytes[0] ^= 0xFF;
        var certJson = JsonSerializer.Serialize(new { enc, sig = Convert.ToBase64String(sigBytes), alg = "base64+ed25519+v2" });

        var licenseFile = LicenseFile.Parse(WrapPem(certJson));
        Assert.False(licenseFile.Verify(publicKey));
    }

    [Fact]
    public void Verify_FailsClosed_OnTamperedEncPayload()
    {
        var (publicKey, privateKey) = GenerateKeyPair();
        using var _ = privateKey;
        var payloadJson = BuildPayloadJson(Guid.NewGuid(), "LIC-ABC");
        var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson));
        var sig = Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(privateKey, Encoding.UTF8.GetBytes(enc)));

        var tamperedPayloadJson = BuildPayloadJson(Guid.NewGuid(), "LIC-XYZ");
        var tamperedEnc = Convert.ToBase64String(Encoding.UTF8.GetBytes(tamperedPayloadJson));
        var certJson = JsonSerializer.Serialize(new { enc = tamperedEnc, sig, alg = "base64+ed25519+v2" });

        var licenseFile = LicenseFile.Parse(WrapPem(certJson));
        Assert.False(licenseFile.Verify(publicKey));
    }

    [Fact]
    public void Verify_FailsClosed_WhenSignatureCoversDecodedBytesInsteadOfBase64String()
    {
        // CRITICAL regression test: the server (and this SDK) signs/verifies over `enc`'s base64
        // STRING bytes, never the base64-decoded payload bytes. This fixture is deliberately
        // signed the WRONG way (over decoded bytes) to prove Verify() rejects it — if this test
        // ever starts passing as true, LicenseFile.Verify has regressed to the unsafe behavior.
        var (publicKey, privateKey) = GenerateKeyPair();
        using var _ = privateKey;
        var payloadJson = BuildPayloadJson(Guid.NewGuid(), "LIC-ABC");
        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
        var enc = Convert.ToBase64String(payloadBytes);

        var wrongSignature = SignatureAlgorithm.Ed25519.Sign(privateKey, payloadBytes); // decoded bytes — WRONG
        var certJson = JsonSerializer.Serialize(new { enc, sig = Convert.ToBase64String(wrongSignature), alg = "base64+ed25519+v2" });

        var licenseFile = LicenseFile.Parse(WrapPem(certJson));
        Assert.False(licenseFile.Verify(publicKey));
    }

    /// <summary>
    /// D17: the format gate is a property of the file, not of the entry point. Before, a v1 file
    /// produced UnsupportedAlgorithmException from Verify/VerifyAndDecrypt (alg lacks ed25519),
    /// UnsupportedAlgorithmException from DecodePayloadJson (no +v2) or OfflineFileFormatException
    /// from ParsePayload (no meta) depending on which method ran first and which malformation it
    /// hit first. Now Parse refuses it, before any key exists to verify with.
    /// </summary>
    [Theory]
    [InlineData("base64+ed25519")]            // pre-v2
    [InlineData("aes-256-gcm+ed25519")]       // pre-v2, encrypted
    [InlineData("rsa-sha256")]                // no grammar at all
    [InlineData("base64+ed25519+v3")]
    [InlineData("base64+ed25519+V2")]
    [InlineData("base64+ed25519+v2junk")]
    [InlineData("xbase64+ed25519+v2")]
    [InlineData("rot13+ed25519+v2")]
    [InlineData("base64+rsa-sha256+v2")]      // right grammar, not Ed25519
    [InlineData("base64-ed25519-v2")]
    [InlineData("base64")]
    [InlineData("")]
    public void Parse_RefusesAnAlgThatIsNotEd25519FormatV2_BeforeAnySignatureWork(string alg)
    {
        var (_, privateKey) = GenerateKeyPair();
        using var _ = privateKey;
        var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(BuildPayloadJson(Guid.NewGuid(), "LIC-ABC")));
        // Correctly signed on purpose: the refusal must not depend on the signature being bad.
        var pem = BuildValidPem(privateKey, enc, alg);

        Assert.Throws<UnsupportedAlgorithmException>(() => LicenseFile.Parse(pem));
    }

    [Theory]
    [InlineData("base64+ed25519+v2")]
    [InlineData("aes-256-gcm+ed25519+v2")]
    public void Parse_AcceptsExactlyTheTwoAlgsTheServerEmits(string alg)
    {
        var (_, privateKey) = GenerateKeyPair();
        using var _ = privateKey;
        var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(BuildPayloadJson(Guid.NewGuid(), "LIC-ABC")));

        Assert.Equal(alg, LicenseFile.Parse(BuildValidPem(privateKey, enc, alg)).Certificate.Alg);
    }

    [Fact]
    public void Parse_Throws_OnMissingPemMarkers()
    {
        Assert.Throws<OfflineFileFormatException>(() => LicenseFile.Parse("not a pem file"));
    }

    [Fact]
    public void Parse_Throws_OnInvalidBase64Body()
    {
        Assert.Throws<OfflineFileFormatException>(() =>
            LicenseFile.Parse("-----BEGIN LICENSE FILE-----\n***not base64***\n-----END LICENSE FILE-----"));
    }

    [Fact]
    public void Parse_Throws_OfflineFileFormatException_NotArgumentOutOfRangeException_OnOverlappingMarkers()
    {
        // Security-review regression: a string short enough that the begin/end markers "overlap"
        // (it independently satisfies StartsWith(begin) and EndsWith(end) while being shorter than
        // begin.Length + end.Length) must still fail with the documented OfflineFileFormatException,
        // not an untyped ArgumentOutOfRangeException from the naive slice.
        const string overlapping = "-----BEGIN LICENSE FILE---------END LICENSE FILE-----";
        Assert.Throws<OfflineFileFormatException>(() => LicenseFile.Parse(overlapping));
    }

    [Fact]
    public void VerifyAndDecrypt_Throws_WhenSignatureInvalid()
    {
        var (publicKey, privateKey) = GenerateKeyPair();
        using var _ = privateKey;
        var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(BuildPayloadJson(Guid.NewGuid(), "LIC-ABC")));
        var sigBytes = SignatureAlgorithm.Ed25519.Sign(privateKey, Encoding.UTF8.GetBytes(enc));
        sigBytes[0] ^= 0xFF;
        var certJson = JsonSerializer.Serialize(new { enc, sig = Convert.ToBase64String(sigBytes), alg = "base64+ed25519+v2" });

        var licenseFile = LicenseFile.Parse(WrapPem(certJson));
        Assert.Throws<SignatureVerificationException>(() => licenseFile.VerifyAndDecrypt(publicKey, "unused"));
    }

    [Fact]
    public void VerifyAndDecrypt_Throws_WhenEncryptedWithWrongLicenseKey()
    {
        var (publicKey, privateKey) = GenerateKeyPair();
        using var _ = privateKey;
        var payloadJson = BuildPayloadJson(Guid.NewGuid(), "LIC-ABC");
        var plaintext = Encoding.UTF8.GetBytes(payloadJson);
        var nonce = new byte[AesGcmCipher.NonceLength];
        var aesKey = Hkdf.DeriveLicenseFileKey("correct-key");
        var (ciphertext, tag) = AesGcmCipher.Seal(aesKey, nonce, plaintext);
        var payload = new byte[nonce.Length + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, payload, nonce.Length, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length + ciphertext.Length, tag.Length);
        var enc = Convert.ToBase64String(payload);
        var pem = BuildValidPem(privateKey, enc, "aes-256-gcm+ed25519+v2");

        var licenseFile = LicenseFile.Parse(pem);
        // A verified signature followed by an AES-GCM failure is the wrong license key, not a
        // forgery (D16). Still a SignatureVerificationException for a 2.1.1 catch clause.
        var ex = Assert.Throws<LicenseKeyMismatchException>(() => licenseFile.VerifyAndDecrypt(publicKey, "wrong-key"));
        Assert.IsAssignableFrom<SignatureVerificationException>(ex);
    }

    // ── Format v2: expiry inside the signature ───────────────────────────────

    private const long Exp = 1_767_229_200;

    [Fact]
    public void AnExpiredFileIsRefusedEvenThoughItsSignatureIsValid()
    {
        // The whole point of v2. In v1 the requested TTL lived only in the JSON:API envelope
        // around the certificate, so a 24-hour trial file stayed cryptographically valid forever
        // and the client — which is the attacker — simply kept the PEM.
        var (publicKey, privateKey) = GenerateKeyPair();
        using var _ = privateKey;
        var enc = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(BuildPayloadJson(Guid.NewGuid(), "K", Exp)));
        var pem = BuildValidPem(privateKey, enc, "base64+ed25519+v2");

        var licenseFile = LicenseFile.Parse(pem);
        var ex = Assert.Throws<LicenseFileExpiredException>(
            () => licenseFile.VerifyAndDecrypt(publicKey, "K", Exp + 3600));
        Assert.Equal(Exp, ex.ExpiresAt);
    }

    [Fact]
    public void AFileWithinItsTtlVerifies()
    {
        var (publicKey, privateKey) = GenerateKeyPair();
        using var _ = privateKey;
        var enc = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(BuildPayloadJson(Guid.NewGuid(), "K", Exp)));
        var pem = BuildValidPem(privateKey, enc, "base64+ed25519+v2");

        var (_license, claims) = LicenseFile.Parse(pem).VerifyWithClaims(publicKey, "K", Exp - 3600);
        Assert.Equal(Exp, claims.ExpiresAt);
        Assert.Equal("test-jti", claims.Id);
        Assert.Equal("test-kid", claims.KeyId);
    }

    [Fact]
    public void AFileWithoutAnExpClaimNeverExpires()
    {
        // Checkout without a `ttl` produces no `exp`. That must read as perpetual, not as
        // "expired at the epoch".
        var (publicKey, privateKey) = GenerateKeyPair();
        using var _ = privateKey;
        var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(BuildPayloadJson(Guid.NewGuid(), "K")));
        var pem = BuildValidPem(privateKey, enc, "base64+ed25519+v2");

        var (_license, claims) = LicenseFile.Parse(pem)
            .VerifyWithClaims(publicKey, "K", long.MaxValue / 2);
        Assert.Null(claims.ExpiresAt);
    }

    [Fact]
    public void AV1AlgIsRefusedOutright_AtParse()
    {
        // Accepting both formats would hand back the permanent-file problem: any certificate
        // issued before v2 could be kept and reused forever. The refusal now lands at Parse, so
        // a v1 file cannot even reach a verifier — and CheckOutLicenseAsync, which parses the
        // server's certificate, surfaces it at checkout time.
        var (_, privateKey) = GenerateKeyPair();
        using var _ = privateKey;
        var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(BuildPayloadJson(Guid.NewGuid(), "K")));
        var pem = BuildValidPem(privateKey, enc, "base64+ed25519");

        Assert.Throws<UnsupportedAlgorithmException>(() => LicenseFile.Parse(pem));
    }

    [Fact]
    public void AV1PayloadWithoutMetaIsRefused()
    {
        // Second line behind the alg gate: a file must not reach the expiry check with nothing to
        // check.
        var (publicKey, privateKey) = GenerateKeyPair();
        using var _ = privateKey;
        var v1Payload = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["type"] = "licenses",
                ["id"] = Guid.NewGuid().ToString(),
                ["attributes"] = new JsonObject { ["key"] = "K" },
            },
        }.ToJsonString();
        var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(v1Payload));
        var pem = BuildValidPem(privateKey, enc, "base64+ed25519+v2");

        var ex = Assert.Throws<OfflineFileFormatException>(
            () => LicenseFile.Parse(pem).VerifyAndDecrypt(publicKey, "K"));
        Assert.Contains("pre-v2", ex.Message, StringComparison.Ordinal);
    }
}
