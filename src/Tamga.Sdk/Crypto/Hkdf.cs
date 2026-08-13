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
/// Both offline file formats derive their AES key here as of format v2, but with different
/// parameters — do not conflate the two paths. Machine file decryption requires BOTH the license
/// key AND the target machine's fingerprint, so a machine file cannot be opened anywhere but on
/// the machine it was issued for; a license file is not bound to a machine and uses a fixed
/// <c>info</c>.
///
/// Before v2 the license-file key was not derived at all: it was the license key's raw UTF-8 bytes
/// zero-padded to 32, which meant an attacker holding a stolen <c>.lic</c> was attacking the
/// license key's own entropy rather than a 256-bit key space. The <c>NaiveKey</c> class that
/// implemented it has been removed rather than deprecated — leaving it public would let a caller
/// silently keep using the weaker derivation.
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

    /// <summary>Fixed salt for license-file key derivation, matching the server exactly.</summary>
    public static readonly byte[] LicenseFileSalt = Encoding.UTF8.GetBytes("tamga:license-file-key-v1");

    /// <summary>Fixed <c>info</c> for license-file key derivation.</summary>
    public static readonly byte[] LicenseFileInfo = Encoding.UTF8.GetBytes("license-file");

    /// <summary>Derives the 32-byte AES key from the license key (as IKM) and machine fingerprint (as info).</summary>
    public static byte[] DeriveMachineFileKey(string licenseKey, string fingerprint)
    {
        var ikm = Encoding.UTF8.GetBytes(licenseKey);
        var info = Encoding.UTF8.GetBytes(fingerprint);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, KeyLength, Salt, info);
    }

    /// <summary>
    /// Derives the 32-byte AES key for an encrypted <c>.lic</c> file:
    /// <c>salt = "tamga:license-file-key-v1"</c>, <c>ikm = licenseKey</c>,
    /// <c>info = "license-file"</c>. No fingerprint — a license file is not bound to a machine.
    /// </summary>
    public static byte[] DeriveLicenseFileKey(string licenseKey)
    {
        var ikm = Encoding.UTF8.GetBytes(licenseKey);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, KeyLength, LicenseFileSalt, LicenseFileInfo);
    }
}
