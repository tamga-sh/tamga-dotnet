using System.Security.Cryptography;

namespace Tamga.Sdk.Crypto;

/// <summary>
/// RSA-2048 signature verification (PKCS#1 v1.5 and PSS, both SHA-256) via BCL
/// <see cref="System.Security.Cryptography.RSA"/> — no third-party dependency needed (available
/// since .NET Core 3.0).
/// </summary>
/// <remarks>
/// Used by <see cref="Checkout.MachineFile"/> (PKCS1 and PSS branches of the scheme-dispatched
/// machine-file verifier) and <see cref="MachineProof"/> (offline proof verification is
/// ALWAYS RSA-2048 PKCS#1 v1.5 / SHA-256, regardless of the license's <c>LicenseScheme</c>).
/// </remarks>
public static class Rsa
{
    /// <summary>Verifies an RSA-PKCS#1 v1.5/SHA-256 signature.</summary>
    public static bool VerifyPkcs1(RSA publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature) =>
        publicKey.VerifyData(message, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    /// <summary>Verifies an RSA-PSS/SHA-256 signature.</summary>
    public static bool VerifyPss(RSA publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature) =>
        publicKey.VerifyData(message, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
}
