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
        Assert.Throws<SignatureVerificationException>(() =>
            machineFile.VerifyAndDecrypt(LicenseScheme.Ed25519Sign, publicKeyBytes, licenseKey, "fp-wrong", IssuedAt));
    }

    [Fact]
    public void Verify_Throws_SchemeNotSupported_ForRsaJwtRs256()
    {
        var payloadJson = BuildPayloadJson(Guid.NewGuid(), "fp-abc");
        var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson));
        // `alg` is deliberately junk here: the refusal must land BEFORE anything parses it, so a
        // file that could never pass the format gate still fails on the scheme first.
        var pem = BuildPem(enc, new byte[64], "rsa-sha256");
        var machineFile = MachineFile.Parse(pem);

        Assert.Throws<SchemeNotSupportedException>(() => machineFile.Verify(LicenseScheme.Rsa2048JwtRs256, new byte[32]));
    }

    [Fact]
    public void VerifyAndDecrypt_Throws_SchemeNotSupported_ForRsaJwtRs256_BeforeAttemptingDecryption()
    {
        var payloadJson = BuildPayloadJson(Guid.NewGuid(), "fp-abc");
        var enc = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson));
        // Same point as above, and additionally pre-v2: neither gate may fire before the scheme
        // refusal, or the error a caller sees depends on which malformation they hit first.
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
