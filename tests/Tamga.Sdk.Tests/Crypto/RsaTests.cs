using System.Security.Cryptography;
using Tamga.Sdk.Crypto;
using Xunit;

namespace Tamga.Sdk.Tests.Crypto;

public class RsaTests
{
    private static RSA CreateKey() => RSA.Create(2048);

    [Fact]
    public void VerifyPkcs1_ReturnsTrue_ForValidSignature()
    {
        using var rsa = CreateKey();
        var message = "offline proof payload"u8.ToArray();
        var signature = rsa.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        Assert.True(Rsa.VerifyPkcs1(rsa, message, signature));
    }

    [Fact]
    public void VerifyPkcs1_ReturnsFalse_ForTamperedMessage()
    {
        using var rsa = CreateKey();
        var message = "offline proof payload"u8.ToArray();
        var signature = rsa.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        Assert.False(Rsa.VerifyPkcs1(rsa, "tampered payload"u8.ToArray(), signature));
    }

    [Fact]
    public void VerifyPss_ReturnsTrue_ForValidSignature()
    {
        using var rsa = CreateKey();
        var message = "machine file payload"u8.ToArray();
        var signature = rsa.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

        Assert.True(Rsa.VerifyPss(rsa, message, signature));
    }

    [Fact]
    public void VerifyPss_ReturnsFalse_ForTamperedSignature()
    {
        using var rsa = CreateKey();
        var message = "machine file payload"u8.ToArray();
        var signature = rsa.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        signature[0] ^= 0xFF;

        Assert.False(Rsa.VerifyPss(rsa, message, signature));
    }

    [Fact]
    public void VerifyPkcs1_ReturnsFalse_ForPssSignature()
    {
        // Cross-padding confusion must fail closed.
        using var rsa = CreateKey();
        var message = "padding confusion check"u8.ToArray();
        var pssSignature = rsa.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

        Assert.False(Rsa.VerifyPkcs1(rsa, message, pssSignature));
    }

    /// <summary>
    /// Locks in the fail-closed contract for a wrong-length signature against a validly
    /// imported RSA-2048 key -- same bug class as the already-fixed PemEnvelope.Strip HIGH
    /// finding, in the signature-length dimension. Found no defect (BCL RSA.VerifyData
    /// returns false, doesn't throw), but no prior test exercised this shape -- added per
    /// the audit's recommendation.
    /// </summary>
    [Fact]
    public void VerifyPkcs1_ReturnsFalse_ForWrongLengthSignature()
    {
        using var rsa = CreateKey();
        var tooShort = new byte[3]; // RSA-2048 signatures are always 256 bytes

        Assert.False(Rsa.VerifyPkcs1(rsa, "machine file payload"u8.ToArray(), tooShort));
    }

    [Fact]
    public void VerifyPss_ReturnsFalse_ForWrongLengthSignature()
    {
        using var rsa = CreateKey();
        var tooShort = new byte[3];

        Assert.False(Rsa.VerifyPss(rsa, "machine file payload"u8.ToArray(), tooShort));
    }

    // ── TryImportPublicKey ───────────────────────────────────────────────────

    /// <summary>
    /// The server emits BOTH encodings for the same key — X.509 SubjectPublicKeyInfo from the
    /// account resource, PKCS#1 RSAPublicKey from <c>license_signing::extract_public_key</c> — so
    /// accepting only the first would fail every PKCS#1 caller with a result indistinguishable
    /// from a forged file.
    /// </summary>
    [Fact]
    public void TryImportPublicKey_AcceptsBothEncodingsTheServerCanProduce()
    {
        using var key = CreateKey();
        var message = "machine file payload"u8.ToArray();
        var signature = key.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        using var fromSpki = Rsa.TryImportPublicKey(key.ExportSubjectPublicKeyInfo());
        Assert.NotNull(fromSpki);
        Assert.True(Rsa.VerifyPkcs1(fromSpki!, message, signature));

        using var fromPkcs1 = Rsa.TryImportPublicKey(key.ExportRSAPublicKey());
        Assert.NotNull(fromPkcs1);
        Assert.True(Rsa.VerifyPkcs1(fromPkcs1!, message, signature));
    }

    /// <summary>
    /// Both import attempts fail: SPKI first, then PKCS#1. The result is <see langword="null"/> —
    /// "these bytes are not a key" — rather than an exception, so the caller fails closed on a
    /// verification result instead of on a crash.
    /// </summary>
    [Fact]
    public void TryImportPublicKey_ReturnsNull_WhenBothEncodingsFail()
    {
        Assert.Null(Rsa.TryImportPublicKey(new byte[] { 0x30, 0x00, 0xFF }));
        Assert.Null(Rsa.TryImportPublicKey(ReadOnlySpan<byte>.Empty));

        // A structurally valid DER key of the WRONG algorithm is the realistic version of this: an
        // ECDSA SPKI is well-formed ASN.1, so it gets past the parser and is refused on algorithm.
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Null(Rsa.TryImportPublicKey(ecdsa.ExportSubjectPublicKeyInfo()));
    }

}
