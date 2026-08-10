using NSec.Cryptography;

namespace Tamga.Sdk.Crypto;

/// <summary>
/// Ed25519 signature verification, backed by <c>NSec.Cryptography</c> (a libsodium binding).
/// </summary>
/// <remarks>
/// GOTCHA: <c>System.Security.Cryptography</c> only gets native Ed25519 support in .NET 9+. Since
/// this SDK targets <c>net8.0</c> exclusively for v0.1, this type MUST use
/// <c>NSec.Cryptography</c> — do not "simplify" by reaching for a BCL Ed25519 type; it does not
/// exist on net8.0 and the build will not compile against it. See CLAUDE.md "Cryptography Backend".
///
/// Used by <see cref="Checkout.LicenseFile"/> (Ed25519 is the ONLY signature scheme for license
/// checkout files) and <see cref="Checkout.MachineFile"/> (the Ed25519 branch of its
/// scheme-dispatched verifier).
/// </remarks>
public static class Ed25519
{
    private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;

    /// <summary>
    /// Verifies <paramref name="signature"/> over <paramref name="message"/> using the given raw
    /// 32-byte Ed25519 public key. Returns <see langword="false"/> (never throws) for a malformed
    /// key or a failed verification — callers must fail closed on a <see langword="false"/> result.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (!PublicKey.TryImport(Algorithm, publicKey, KeyBlobFormat.RawPublicKey, out var key) || key is null)
        {
            return false;
        }

        return Algorithm.Verify(key, message, signature);
    }
}
