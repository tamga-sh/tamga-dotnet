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

    /// <summary>
    /// Imports an RSA public key in either DER encoding the server hands out, returning
    /// <see langword="null"/> (never throwing) if the bytes are neither.
    /// </summary>
    /// <remarks>
    /// The server produces BOTH encodings for the same key and which one a caller holds depends on
    /// where they got it:
    /// <list type="bullet">
    /// <item><description>X.509 <c>SubjectPublicKeyInfo</c> — the account resource's
    /// <c>public_key</c> attribute, written from aws-lc-rs <c>PublicKey::as_der()</c>.</description></item>
    /// <item><description>PKCS#1 <c>RSAPublicKey</c> (RFC 8017, 270 bytes for RSA-2048, starting
    /// <c>30 82 01 0A</c>) — what <c>license_signing::extract_public_key</c> returns, via
    /// <c>PublicKey::as_ref()</c>.</description></item>
    /// </list>
    /// Accepting only SPKI silently fails every PKCS#1 caller with a result indistinguishable from
    /// a forged file. Probing is safe here: the key is caller-supplied and trusted, and neither
    /// import path can widen what the signature check then proves.
    /// </remarks>
    public static RSA? TryImportPublicKey(ReadOnlySpan<byte> publicKey)
    {
        // A fresh instance per attempt: a failed import can leave the previous one part-populated.
        var spki = RSA.Create();
        try
        {
            spki.ImportSubjectPublicKeyInfo(publicKey, out _);
            return spki;
        }
        catch (Exception ex) when (IsMalformedKey(ex))
        {
            spki.Dispose();
        }
        catch
        {
            // Anything else is a real fault, not "wrong encoding" — dispose, then let it out
            // rather than quietly falling through to the second attempt.
            spki.Dispose();
            throw;
        }

        var pkcs1 = RSA.Create();
        try
        {
            pkcs1.ImportRSAPublicKey(publicKey, out _);
            return pkcs1;
        }
        catch (Exception ex) when (IsMalformedKey(ex))
        {
            pkcs1.Dispose();
            return null;
        }
        catch
        {
            pkcs1.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Whether an exception means "these bytes are not a key in this encoding" rather than a real
    /// fault. DER parsing surfaces both of these depending on where it gives up.
    /// </summary>
    internal static bool IsMalformedKey(Exception ex) =>
        ex is CryptographicException or System.Formats.Asn1.AsnContentException;
}
