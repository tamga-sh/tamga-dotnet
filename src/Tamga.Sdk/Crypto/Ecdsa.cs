using System.Security.Cryptography;

namespace Tamga.Sdk.Crypto;

/// <summary>
/// ECDSA P-256/SHA-256 signature verification via BCL
/// <see cref="System.Security.Cryptography.ECDsa"/> — no third-party dependency needed (available
/// since .NET Core 3.0).
/// </summary>
/// <remarks>Used by <c>Checkout.MachineFile</c> — the ECDSA-P256 branch of the scheme-dispatched machine-file verifier.</remarks>
public static class Ecdsa
{
    /// <summary>NIST P-256 (secp256r1) curve OID — see <see cref="Verify"/>'s curve-enforcement check.</summary>
    private const string P256Oid = "1.2.840.10045.3.1.7";

    /// <summary>
    /// Verifies an ECDSA P-256/SHA-256 signature in ASN.1 DER form
    /// (<see cref="DSASignatureFormat.Rfc3279DerSequence"/> — a <c>SEQUENCE { r INTEGER,
    /// s INTEGER }</c>), which is what the server produces.
    /// </summary>
    /// <remarks>
    /// FORMAT: the server signs with <c>ECDSA_P256_SHA256_ASN1_SIGNING</c>, so the signature on
    /// the wire is DER, not the raw <c>(r, s)</c> concatenation of
    /// <see cref="DSASignatureFormat.IeeeP1363FixedFieldConcatenation"/>. Verifying with the wrong
    /// format does not throw — it simply returns <see langword="false"/> for every genuine
    /// signature, which is a silent, total verification failure that looks exactly like a forged
    /// file. This branch is latent today (no write path sets a license's <c>scheme</c>, so every
    /// machine file is currently Ed25519), but it turns on the moment that column is populated.
    ///
    /// SECURITY: <paramref name="publicKey"/>'s curve is whatever the caller constructed it with —
    /// when built via <c>ImportSubjectPublicKeyInfo</c> (the machine-file path), the curve comes
    /// from the input bytes' own embedded OID. Without checking it here, a validly-signed message
    /// from any other curve (e.g. P-384) would verify successfully, since SHA-256 is just the
    /// digest algorithm and is independent of curve choice. Found via audit; see
    /// <c>EcdsaTests.Verify_ReturnsFalse_WhenKeyItselfIsNotP256</c> for the regression coverage.
    /// </remarks>
    public static bool Verify(ECDsa publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.KeySize != 256 || publicKey.ExportParameters(false).Curve.Oid.Value != P256Oid)
        {
            return false;
        }
        return publicKey.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }
}
