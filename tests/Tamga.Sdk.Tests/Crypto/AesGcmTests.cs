using System.Security.Cryptography;
using Tamga.Sdk.Crypto;
using Xunit;

namespace Tamga.Sdk.Tests.Crypto;

public class AesGcmTests
{
    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    [Fact]
    public void SealThenOpen_RoundTrips()
    {
        var key = RandomBytes(32);
        var nonce = RandomBytes(AesGcmCipher.NonceLength);
        var plaintext = "the quick brown fox"u8.ToArray();

        var (ciphertext, tag) = AesGcmCipher.Seal(key, nonce, plaintext);
        var decrypted = AesGcmCipher.Open(key, nonce, ciphertext, tag);

        Assert.Equal(plaintext, decrypted);
        Assert.Equal(AesGcmCipher.TagLength, tag.Length);
    }

    [Fact]
    public void Open_FailsClosed_OnTamperedCiphertext()
    {
        var key = RandomBytes(32);
        var nonce = RandomBytes(AesGcmCipher.NonceLength);
        var plaintext = "sensitive payload"u8.ToArray();
        var (ciphertext, tag) = AesGcmCipher.Seal(key, nonce, plaintext);
        ciphertext[0] ^= 0xFF;

        Assert.Throws<AuthenticationTagMismatchException>(() => AesGcmCipher.Open(key, nonce, ciphertext, tag));
    }

    [Fact]
    public void Open_FailsClosed_OnTamperedTag()
    {
        var key = RandomBytes(32);
        var nonce = RandomBytes(AesGcmCipher.NonceLength);
        var plaintext = "sensitive payload"u8.ToArray();
        var (ciphertext, tag) = AesGcmCipher.Seal(key, nonce, plaintext);
        tag[0] ^= 0xFF;

        Assert.Throws<AuthenticationTagMismatchException>(() => AesGcmCipher.Open(key, nonce, ciphertext, tag));
    }

    [Fact]
    public void Open_FailsClosed_OnWrongKey()
    {
        var key = RandomBytes(32);
        var wrongKey = RandomBytes(32);
        var nonce = RandomBytes(AesGcmCipher.NonceLength);
        var plaintext = "sensitive payload"u8.ToArray();
        var (ciphertext, tag) = AesGcmCipher.Seal(key, nonce, plaintext);

        Assert.Throws<AuthenticationTagMismatchException>(() => AesGcmCipher.Open(wrongKey, nonce, ciphertext, tag));
    }
}
