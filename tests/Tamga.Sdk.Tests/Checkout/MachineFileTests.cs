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

public class MachineFileTests
{
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
    }.ToJsonString();

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
        var machine = machineFile.VerifyAndDecrypt(scheme, publicKeyBytes, licenseKey: "unused", fingerprint: "unused");
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
        var (ciphertext, tag) = AesGcmCipher.Seal(aesKey, nonce, plaintext);
        var payload = new byte[nonce.Length + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, payload, nonce.Length, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length + ciphertext.Length, tag.Length);
        var enc = Convert.ToBase64String(payload);

        var (publicKeyBytes, sign, _) = MakeSigner(LicenseScheme.Ed25519Sign);
        var signature = sign(Encoding.UTF8.GetBytes(enc));
        var pem = BuildPem(enc, signature, "aes-256-gcm+ed25519");

        var machineFile = MachineFile.Parse(pem);
        var machine = machineFile.VerifyAndDecrypt(LicenseScheme.Ed25519Sign, publicKeyBytes, licenseKey, fingerprint);
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
        var (ciphertext, tag) = AesGcmCipher.Seal(aesKey, nonce, plaintext);
        var payload = new byte[nonce.Length + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, payload, nonce.Length, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length + ciphertext.Length, tag.Length);
        var enc = Convert.ToBase64String(payload);

        var (publicKeyBytes, sign, _) = MakeSigner(LicenseScheme.Ed25519Sign);
        var signature = sign(Encoding.UTF8.GetBytes(enc));
        var pem = BuildPem(enc, signature, "aes-256-gcm+ed25519");

        var machineFile = MachineFile.Parse(pem);
        Assert.Throws<SignatureVerificationException>(() =>
            machineFile.VerifyAndDecrypt(LicenseScheme.Ed25519Sign, publicKeyBytes, licenseKey, "fp-wrong"));
    }

    [Fact]
    public void Verify_Throws_SchemeNotSupported_ForRsaJwtRs256()
    {
        var payloadJson = BuildPayloadJson(Guid.NewGuid(), "fp-abc");
        var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson));
        var pem = BuildPem(enc, new byte[64], "rsa-sha256");
        var machineFile = MachineFile.Parse(pem);

        Assert.Throws<SchemeNotSupportedException>(() => machineFile.Verify(LicenseScheme.Rsa2048JwtRs256, new byte[32]));
    }

    [Fact]
    public void VerifyAndDecrypt_Throws_SchemeNotSupported_ForRsaJwtRs256_BeforeAttemptingDecryption()
    {
        var payloadJson = BuildPayloadJson(Guid.NewGuid(), "fp-abc");
        var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson));
        var pem = BuildPem(enc, new byte[64], "aes-256-gcm+rsa-sha256");
        var machineFile = MachineFile.Parse(pem);

        Assert.Throws<SchemeNotSupportedException>(() =>
            machineFile.VerifyAndDecrypt(LicenseScheme.Rsa2048JwtRs256, new byte[32], "key", "fp"));
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
                    return (publicKey, msg => SignatureAlgorithm.Ed25519.Sign(key, msg), "base64+ed25519");
                }

            case LicenseScheme.Rsa2048Pkcs1Sign:
                {
                    var rsa = RSA.Create(2048);
                    var publicKey = rsa.ExportSubjectPublicKeyInfo();
                    return (publicKey, msg => rsa.SignData(msg, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), "base64+rsa-sha256");
                }

            case LicenseScheme.Rsa2048Pkcs1PssSign:
                {
                    var rsa = RSA.Create(2048);
                    var publicKey = rsa.ExportSubjectPublicKeyInfo();
                    return (publicKey, msg => rsa.SignData(msg, HashAlgorithmName.SHA256, RSASignaturePadding.Pss), "base64+rsa-pss-sha256");
                }

            case LicenseScheme.EcdsaP256Sign:
                {
                    var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                    var publicKey = ecdsa.ExportSubjectPublicKeyInfo();
                    return (publicKey, msg => ecdsa.SignData(msg, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation), "base64+ecdsa-p256");
                }

            default:
                throw new NotSupportedException($"No test signer for {scheme}.");
        }
    }
}
