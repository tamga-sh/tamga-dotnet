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

    /// <summary>Length of a SEC1 uncompressed P-256 point: the <c>0x04</c> marker plus X and Y.</summary>
    private const int UncompressedP256PointLength = 65;

    /// <summary>The SEC1 marker byte that introduces an uncompressed point.</summary>
    private const byte UncompressedPointMarker = 0x04;

    /// <summary>Length of one P-256 coordinate.</summary>
    private const int P256CoordinateLength = 32;

    /// <summary>
    /// Imports an ECDSA public key in either encoding the server hands out, returning
    /// <see langword="null"/> (never throwing) if the bytes are neither.
    /// </summary>
    /// <remarks>
    /// The server's ECDSA public key is a RAW 65-byte SEC1 uncompressed point
    /// (<c>0x04 || X || Y</c>) — that is what aws-lc-rs <c>EcdsaKeyPair::public_key().as_ref()</c>
    /// returns, and it is what both <c>key_material</c> and
    /// <c>license_signing::extract_public_key</c> store and hand out. It is NOT
    /// <c>SubjectPublicKeyInfo</c> DER, so importing only SPKI rejects every real server key with
    /// a failure that looks exactly like a forged file. SPKI is still accepted, for a caller who
    /// converted the key themselves.
    ///
    /// SECURITY: on the raw-point path the curve is pinned to P-256 HERE rather than read out of
    /// the input, so attacker-supplied bytes cannot select a different curve. On the SPKI path the
    /// curve does come from the input, which is why <see cref="Verify"/> re-checks it either way.
    /// </remarks>
    public static ECDsa? TryImportPublicKey(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length == UncompressedP256PointLength && publicKey[0] == UncompressedPointMarker)
        {
            var parameters = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint
                {
                    X = publicKey.Slice(1, P256CoordinateLength).ToArray(),
                    Y = publicKey.Slice(1 + P256CoordinateLength, P256CoordinateLength).ToArray(),
                },
            };

            try
            {
                return ECDsa.Create(parameters);
            }
            catch (Exception ex) when (Rsa.IsMalformedKey(ex))
            {
                return null;
            }
        }

        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);
            return ecdsa;
        }
        catch (Exception ex) when (Rsa.IsMalformedKey(ex))
        {
            ecdsa.Dispose();
            return null;
        }
        catch
        {
            // A real fault rather than "wrong encoding": release the handle, then rethrow.
            ecdsa.Dispose();
            throw;
        }
    }

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
    /// file. This is no longer a latent branch: <c>MachineFileFixtureTests</c> runs it against
    /// <c>.machine</c> files the server itself signed with <c>ecdsa-p256</c>, whose signatures are
    /// DER on the wire (<c>30 45 02 20 …</c>) — so the format choice here is now pinned by real
    /// server output rather than by reading the server's source.
    ///
    /// SECURITY: <paramref name="publicKey"/>'s curve is whatever the caller constructed it with —
    /// when built from SPKI DER via <see cref="TryImportPublicKey"/>'s fallback path, the curve
    /// comes from the input bytes' own embedded OID. Without checking it here, a validly-signed message
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
