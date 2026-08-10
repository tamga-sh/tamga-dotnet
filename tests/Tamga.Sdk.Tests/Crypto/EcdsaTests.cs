using System.Security.Cryptography;
using Tamga.Sdk.Crypto;
using Xunit;

namespace Tamga.Sdk.Tests.Crypto;

public class EcdsaTests
{
    private static ECDsa CreateKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    [Fact]
    public void Verify_ReturnsTrue_ForValidSignature()
    {
        using var ecdsa = CreateKey();
        var message = "machine file payload"u8.ToArray();
        var signature = ecdsa.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        Assert.True(Ecdsa.Verify(ecdsa, message, signature));
    }

    [Fact]
    public void Verify_ReturnsFalse_ForTamperedMessage()
    {
        using var ecdsa = CreateKey();
        var message = "machine file payload"u8.ToArray();
        var signature = ecdsa.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        Assert.False(Ecdsa.Verify(ecdsa, "tampered payload"u8.ToArray(), signature));
    }

    [Fact]
    public void Verify_ReturnsFalse_ForWrongCurveKey()
    {
        using var ecdsaP256 = CreateKey();
        using var ecdsaOther = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var message = "curve confusion check"u8.ToArray();
        var signature = ecdsaOther.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        Assert.False(Ecdsa.Verify(ecdsaP256, message, signature));
    }
}
