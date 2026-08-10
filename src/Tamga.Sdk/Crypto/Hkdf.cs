using System.Security.Cryptography;
using System.Text;

namespace Tamga.Sdk.Crypto;

/// <summary>
/// HKDF-SHA256 wrapper (RFC 5869) via BCL <see cref="System.Security.Cryptography.HKDF"/> — no
/// third-party dependency needed (available since .NET Core 3.0). Machine-checkout's real,
/// properly-derived encryption key: 32 bytes from
/// <c>salt = "tamga:machine-file-key-v1"</c>, <c>ikm = &lt;license key&gt;</c>,
/// <c>info = &lt;machine fingerprint&gt;</c>.
/// </summary>
/// <remarks>
/// GOTCHA: this is the machine-checkout key derivation and is a REAL KDF — explicitly NOT the
/// same scheme as license checkout's naive derivation (see <see cref="NaiveKey"/>). Machine file
/// decryption requires BOTH the license key AND the target machine's fingerprint; license file
/// decryption requires only the license key. Do not conflate the two paths — "unifying" them would
/// silently break interop with whichever format wasn't matched.
///
/// Used by <see cref="Checkout.MachineFile"/>, which feeds the derived key into
/// <see cref="AesGcmCipher"/> for the encrypted <c>.machine</c> file payload.
/// </remarks>
public static class Hkdf
{
    /// <summary>Fixed salt for machine-file key derivation, matching the server exactly.</summary>
    public static readonly byte[] Salt = Encoding.UTF8.GetBytes("tamga:machine-file-key-v1");

    /// <summary>The fixed AES-256 key length this derivation always produces.</summary>
    public const int KeyLength = 32;

    /// <summary>Derives the 32-byte AES key from the license key (as IKM) and machine fingerprint (as info).</summary>
    public static byte[] DeriveMachineFileKey(string licenseKey, string fingerprint)
    {
        var ikm = Encoding.UTF8.GetBytes(licenseKey);
        var info = Encoding.UTF8.GetBytes(fingerprint);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, KeyLength, Salt, info);
    }
}
