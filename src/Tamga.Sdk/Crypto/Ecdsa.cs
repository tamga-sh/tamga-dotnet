using System.Security.Cryptography;

namespace Tamga.Sdk.Crypto;

/// <summary>
/// ECDSA P-256/SHA-256 signature verification via BCL
/// <see cref="System.Security.Cryptography.ECDsa"/> — no third-party dependency needed (available
/// since .NET Core 3.0).
/// </summary>
/// <remarks>Used by <see cref="Checkout.MachineFile"/> — the ECDSA-P256 branch of the scheme-dispatched machine-file verifier.</remarks>
public static class Ecdsa
{
    /// <summary>
    /// Verifies an ECDSA P-256/SHA-256 signature over raw <c>(r, s)</c> concatenated bytes
    /// (<see cref="DSASignatureFormat.IeeeP1363FixedFieldConcatenation"/> — the conventional wire
    /// format for this scheme, as opposed to DER-encoded ASN.1).
    /// </summary>
    public static bool Verify(ECDsa publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature) =>
        publicKey.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
}
