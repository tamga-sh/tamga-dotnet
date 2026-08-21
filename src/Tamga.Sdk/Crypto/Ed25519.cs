using System.Security.Cryptography;
using System.Text;
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

    /// <summary>
    /// The number of leading SHA-256 bytes that make up a <c>kid</c> — eight bytes, so sixteen
    /// lowercase hex characters.
    /// </summary>
    public const int KeyIdByteLength = 8;

    /// <summary>
    /// The <c>kid</c> the server publishes for an account whose <c>ed25519_public_key</c> column
    /// was never populated: <c>SHA-256("")</c>, truncated. Recognising it distinguishes "this
    /// server published no key at all" from "my key set is stale".
    /// </summary>
    /// <remarks>
    /// Both checkout handlers compute the claim as
    /// <c>key_id(account.ed25519_public_key.as_deref().unwrap_or_default())</c>
    /// (<c>check_out_license.rs:95-97</c>, <c>check_out_machine.rs:127-129</c>), so an unbackfilled
    /// account signs <em>every</em> file it issues with this one value. No key set can ever contain
    /// it — the empty string is not a valid public key — so a file naming it is unverifiable by
    /// construction, and saying so is far more useful than reporting a stale key set.
    /// </remarks>
    public const string UnpublishedAccountKeyId = "e3b0c44298fc1c14";

    /// <summary>
    /// The stable short id of a published signing key: the first eight bytes of SHA-256 over the
    /// public key's <b>base64 string</b>, lowercase hex — sixteen hex characters.
    /// </summary>
    /// <param name="publicKeyBase64">
    /// The public key exactly as the server publishes and stores it — standard base64 of the raw
    /// key bytes. Must NOT be decoded, re-encoded, trimmed, or converted to PEM first; see the
    /// remarks.
    /// </param>
    /// <remarks>
    /// CRITICAL — this mirrors the server's <c>key_id</c>
    /// (<c>shared/crypto/license_file.rs:70-77</c>), and the two details most easily got wrong are
    /// both visible in that function's signature:
    ///
    /// <list type="bullet">
    /// <item><description>
    /// It hashes the <b>UTF-8 bytes of the base64 STRING</b>, never the 32 decoded key bytes.
    /// <c>key_id(ed25519_public_key: &amp;str)</c> takes a <c>&amp;str</c> and calls
    /// <c>.as_bytes()</c> on it. Decoding first is the natural assumption and it is wrong: it
    /// produces a completely different, self-consistent id, so every file reports an unknown
    /// signing key while the code looks correct. <c>SigningKeyIdTests</c> pins both the right
    /// answer and that specific wrong one.
    /// </description></item>
    /// <item><description>
    /// Eight <b>bytes</b> — sixteen hex characters, not eight.
    /// </description></item>
    /// </list>
    ///
    /// Cross-checked against all twelve server-issued machine-file fixtures: every one of their
    /// <c>kid</c> values reproduces from its own <c>public_key_b64</c> under this rule, and none
    /// reproduces from the decoded bytes — across all four signing schemes.
    ///
    /// Computing this locally is a <b>cross-check, not a requirement</b>: the account's key set
    /// serves the same value as each resource's JSON:API <c>id</c>
    /// (<c>accounts/serializer.rs:103-104,122-123</c>). <see cref="Models.SigningKey"/> indexes by
    /// the served id and uses this to detect a disagreement.
    /// </remarks>
    public static string KeyId(string publicKeyBase64)
    {
        ArgumentNullException.ThrowIfNull(publicKeyBase64);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(publicKeyBase64));
        return Convert.ToHexString(digest, 0, KeyIdByteLength).ToLowerInvariant();
    }
}
