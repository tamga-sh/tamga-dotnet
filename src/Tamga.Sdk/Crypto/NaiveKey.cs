using System.Text;

namespace Tamga.Sdk.Crypto;

/// <summary>
/// License-checkout's encryption key derivation, replicated exactly as the server does it
/// (<c>tamga-api/src/features/licenses/check_out_license.rs</c>).
/// </summary>
/// <remarks>
/// CRITICAL: not a KDF and not a hash. It is <c>license.key</c>'s raw UTF-8 bytes, zero-padded
/// (keys shorter than 32 bytes) or truncated (keys longer than 32 bytes) to exactly 32 bytes. A
/// verifier that hashes the key instead of zero-pad/truncating will silently fail to decrypt every
/// encrypted <c>.lic</c> file.
///
/// Contrast with <see cref="Hkdf"/> (machine checkout), which IS a real KDF (HKDF-SHA256) and
/// additionally binds the machine's fingerprint into the derivation — the two paths are not
/// interchangeable.
/// </remarks>
public static class NaiveKey
{
    /// <summary>The fixed AES-256 key length this derivation always produces.</summary>
    public const int KeyLength = 32;

    /// <summary>
    /// Derives the 32-byte AES key from a license key string. CRITICAL: not a KDF — see type-level
    /// remarks. Zero-pads short keys, truncates long keys; never hashes.
    /// </summary>
    public static byte[] Derive(string licenseKey)
    {
        var keyBytes = Encoding.UTF8.GetBytes(licenseKey);
        var result = new byte[KeyLength];
        var copyLength = Math.Min(keyBytes.Length, KeyLength);
        Array.Copy(keyBytes, result, copyLength);
        return result;
    }
}
