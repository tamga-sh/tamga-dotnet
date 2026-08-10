using NSec.Cryptography;
using Xunit;
using TamgaEd25519 = Tamga.Sdk.Crypto.Ed25519;

namespace Tamga.Sdk.Tests.Crypto;

public class Ed25519Tests
{
    private static (byte[] PublicKey, Key PrivateKey) GenerateKeyPair()
    {
        var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var publicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        return (publicKey, key);
    }

    [Fact]
    public void Verify_ReturnsTrue_ForValidSignature()
    {
        var (publicKey, privateKey) = GenerateKeyPair();
        using var _ = privateKey;
        var message = "hello tamga"u8.ToArray();
        var signature = SignatureAlgorithm.Ed25519.Sign(privateKey, message);

        Assert.True(TamgaEd25519.Verify(publicKey, message, signature));
    }

    [Fact]
    public void Verify_ReturnsFalse_ForTamperedMessage()
    {
        var (publicKey, privateKey) = GenerateKeyPair();
        using var _ = privateKey;
        var message = "hello tamga"u8.ToArray();
        var signature = SignatureAlgorithm.Ed25519.Sign(privateKey, message);
        var tampered = "hello tamgb"u8.ToArray();

        Assert.False(TamgaEd25519.Verify(publicKey, tampered, signature));
    }

    [Fact]
    public void Verify_ReturnsFalse_ForTamperedSignature()
    {
        var (publicKey, privateKey) = GenerateKeyPair();
        using var _ = privateKey;
        var message = "hello tamga"u8.ToArray();
        var signature = SignatureAlgorithm.Ed25519.Sign(privateKey, message);
        signature[0] ^= 0xFF;

        Assert.False(TamgaEd25519.Verify(publicKey, message, signature));
    }

    [Fact]
    public void Verify_ReturnsFalse_ForWrongPublicKey()
    {
        var (_, privateKey) = GenerateKeyPair();
        using var _ = privateKey;
        var (otherPublicKey, otherPrivateKey) = GenerateKeyPair();
        using var __ = otherPrivateKey;
        var message = "hello tamga"u8.ToArray();
        var signature = SignatureAlgorithm.Ed25519.Sign(privateKey, message);

        Assert.False(TamgaEd25519.Verify(otherPublicKey, message, signature));
    }

    [Fact]
    public void Verify_ReturnsFalse_ForMalformedPublicKey()
    {
        var badKey = new byte[] { 1, 2, 3 };
        Assert.False(TamgaEd25519.Verify(badKey, "msg"u8.ToArray(), new byte[64]));
    }
}
