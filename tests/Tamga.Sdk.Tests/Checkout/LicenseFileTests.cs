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

    private static string BuildPayloadJson(Guid licenseId, string key) => new JsonObject
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
    }.ToJsonString();

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
        var pem = BuildValidPem(privateKey, enc, "base64+ed25519");

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
        var aesKey = NaiveKey.Derive(licenseKey);
        var (ciphertext, tag) = AesGcmCipher.Seal(aesKey, nonce, plaintext);
        var payload = new byte[nonce.Length + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, payload, nonce.Length, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length + ciphertext.Length, tag.Length);
        var enc = Convert.ToBase64String(payload);

        var pem = BuildValidPem(privateKey, enc, "aes-256-gcm+ed25519");
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
        var certJson = JsonSerializer.Serialize(new { enc, sig = Convert.ToBase64String(sigBytes), alg = "base64+ed25519" });

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
        var certJson = JsonSerializer.Serialize(new { enc = tamperedEnc, sig, alg = "base64+ed25519" });

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
        var certJson = JsonSerializer.Serialize(new { enc, sig = Convert.ToBase64String(wrongSignature), alg = "base64+ed25519" });

        var licenseFile = LicenseFile.Parse(WrapPem(certJson));
        Assert.False(licenseFile.Verify(publicKey));
    }

    [Fact]
    public void Verify_Throws_OnUnsupportedAlgorithm()
    {
        var (publicKey, privateKey) = GenerateKeyPair();
        using var _ = privateKey;
        var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(BuildPayloadJson(Guid.NewGuid(), "LIC-ABC")));
        var sig = Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(privateKey, Encoding.UTF8.GetBytes(enc)));
        var certJson = JsonSerializer.Serialize(new { enc, sig, alg = "rsa-sha256" });

        var licenseFile = LicenseFile.Parse(WrapPem(certJson));
        Assert.Throws<UnsupportedAlgorithmException>(() => licenseFile.Verify(publicKey));
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
        var certJson = JsonSerializer.Serialize(new { enc, sig = Convert.ToBase64String(sigBytes), alg = "base64+ed25519" });

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
        var aesKey = NaiveKey.Derive("correct-key");
        var (ciphertext, tag) = AesGcmCipher.Seal(aesKey, nonce, plaintext);
        var payload = new byte[nonce.Length + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, payload, nonce.Length, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length + ciphertext.Length, tag.Length);
        var enc = Convert.ToBase64String(payload);
        var pem = BuildValidPem(privateKey, enc, "aes-256-gcm+ed25519");

        var licenseFile = LicenseFile.Parse(pem);
        Assert.Throws<SignatureVerificationException>(() => licenseFile.VerifyAndDecrypt(publicKey, "wrong-key"));
    }
}
