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
}
