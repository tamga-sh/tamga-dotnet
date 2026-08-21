using System.Security.Cryptography;
using Tamga.Sdk.Crypto;
using Xunit;

namespace Tamga.Sdk.Tests.Crypto;

public class EcdsaTests
{
    private static ECDsa CreateKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    [Fact]
    public void Verify_ReturnsTrue_ForValidSignature()
    {
        using var ecdsa = CreateKey();
        var message = "machine file payload"u8.ToArray();
        var signature = ecdsa.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        Assert.True(Ecdsa.Verify(ecdsa, message, signature));
    }

    [Fact]
    public void Verify_ReturnsFalse_ForTamperedMessage()
    {
        using var ecdsa = CreateKey();
        var message = "machine file payload"u8.ToArray();
        var signature = ecdsa.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        Assert.False(Ecdsa.Verify(ecdsa, "tampered payload"u8.ToArray(), signature));
    }

    [Fact]
    public void Verify_ReturnsFalse_ForWrongCurveKey()
    {
        using var ecdsaP256 = CreateKey();
        using var ecdsaOther = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var message = "curve confusion check"u8.ToArray();
        var signature = ecdsaOther.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        Assert.False(Ecdsa.Verify(ecdsaP256, message, signature));
    }

    /// <summary>
    /// Regression test for a curve-confusion vulnerability found during a
    /// cross-repo security audit: <see cref="Ecdsa.Verify"/> never checked
    /// the passed-in key's own curve, so a self-consistent P-384 key
    /// verifying its own P-384 signature returned true -- unlike
    /// <see cref="Verify_ReturnsFalse_ForWrongCurveKey"/> above (which tests
    /// a P-384 signature against a *different*, P-256 key -- a key
    /// mismatch, not curve enforcement), this constructs the exact scenario
    /// MachineFile's VerifyEcdsa helper reaches when
    /// <c>ImportSubjectPublicKeyInfo</c> imports a non-P-256
    /// SubjectPublicKeyInfo blob: the resulting <see cref="ECDsa"/> object's
    /// curve is whatever the input bytes declared, and nothing downstream
    /// checked it before this fix.
    /// </summary>
    [Fact]
    public void Verify_ReturnsFalse_WhenKeyItselfIsNotP256()
    {
        using var ecdsaP384 = ECDsa.Create(ECCurve.NamedCurves.nistP384); // deliberately NOT P-256
        var message = "tamga-dotnet ecdsa curve-confusion regression test"u8.ToArray();
        var signature = ecdsaP384.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        Assert.False(Ecdsa.Verify(ecdsaP384, message, signature));
    }

    /// <summary>
    /// Locks in the fail-closed contract for a wrong-length signature against a validly
    /// imported P-256 key -- same bug class as the already-fixed PemEnvelope.Strip HIGH
    /// finding, in the signature-length dimension. Found no defect (BCL ECDsa.VerifyData
    /// returns false, doesn't throw), but no prior test exercised this shape -- added per
    /// the audit's recommendation.
    /// </summary>
    [Fact]
    public void Verify_ReturnsFalse_ForWrongLengthSignature()
    {
        using var ecdsa = CreateKey();
        var tooShort = new byte[3]; // far too short to be a DER SEQUENCE { r, s }

        Assert.False(Ecdsa.Verify(ecdsa, "machine file payload"u8.ToArray(), tooShort));
    }

    /// <summary>
    /// Regression test for the signature-encoding mismatch: the server signs with
    /// <c>ECDSA_P256_SHA256_ASN1_SIGNING</c>, so the wire format is ASN.1 DER, but this verifier
    /// was asking the BCL for IEEE P1363 raw <c>(r, s)</c>. That combination does not throw — it
    /// returns false for every genuine signature, which is a total silent verification failure
    /// indistinguishable from a forged file. Latent only because no server write path currently
    /// sets a license's <c>scheme</c>, so every machine file today is Ed25519.
    /// </summary>
    [Fact]
    public void Verify_AcceptsTheServersDerEncoding_AndRejectsRawP1363()
    {
        using var ecdsa = CreateKey();
        var message = "tamga machine file payload"u8.ToArray();

        var der = ecdsa.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        var raw = ecdsa.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        Assert.True(Ecdsa.Verify(ecdsa, message, der));
        Assert.False(Ecdsa.Verify(ecdsa, message, raw));
    }
}
