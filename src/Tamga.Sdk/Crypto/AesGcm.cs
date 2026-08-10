using BclAesGcm = System.Security.Cryptography.AesGcm;

namespace Tamga.Sdk.Crypto;

/// <summary>
/// AES-256-GCM open/seal wrapper over BCL <see cref="System.Security.Cryptography.AesGcm"/> — no
/// third-party dependency needed (available since .NET Core 3.0). Algorithm-only: never derives
/// the AES key itself. See <see cref="NaiveKey"/> (license checkout) and <see cref="Hkdf"/>
/// (machine checkout) for the two distinct, non-interchangeable key-derivation paths that feed
/// this type.
/// </summary>
public static class AesGcmCipher
{
    /// <summary>Standard AES-GCM nonce length in bytes.</summary>
    public const int NonceLength = 12;

    /// <summary>Standard AES-GCM authentication tag length in bytes.</summary>
    public const int TagLength = 16;

    /// <summary>Decrypts and authenticates a ciphertext. Throws <see cref="System.Security.Cryptography.AuthenticationTagMismatchException"/> (fails closed) on tag mismatch — never returns garbage plaintext for a tampered input.</summary>
    public static byte[] Open(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag)
    {
        var plaintext = new byte[ciphertext.Length];
        using var aes = new BclAesGcm(key, TagLength);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    /// <summary>Encrypts and authenticates a plaintext, producing ciphertext and a separate authentication tag.</summary>
    public static (byte[] Ciphertext, byte[] Tag) Seal(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext)
    {
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];
        using var aes = new BclAesGcm(key, TagLength);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return (ciphertext, tag);
    }
}
