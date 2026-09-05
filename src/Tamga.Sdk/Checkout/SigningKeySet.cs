using Tamga.Sdk.Models;

namespace Tamga.Sdk.Checkout;

/// <summary>
/// The trusted Ed25519 keys an offline file is allowed to have been signed by, indexed by the
/// <c>kid</c> its claims name.
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem this closes.</b> Verifying against one embedded public key collapses two
/// completely different outcomes into one error. A file signed last month, before the account
/// rotated its signing key, is authentic and its license may well still be valid — but it fails
/// against the current key with exactly the error a forgery produces, and the caller has no way to
/// tell "my key set is stale" from "this file was tampered with". The first calls for fetching the
/// key set or shipping an update; the second calls for refusing the customer. Sending support to
/// the wrong one of those is the defect this type exists to fix.
/// </para>
/// <para>
/// <b>How the <c>kid</c> is used, and why that is safe.</b> Every usable key in the set is tried
/// against the signature over <c>enc</c>'s base64 string <em>before</em> a byte of <c>enc</c> is
/// decoded — the same verify-first order the single-key overloads have always used. A file that
/// verifies against any held key is good, and the key that verified it is returned. The
/// <c>kid</c> is read only when no key verified, and only to label the failure: a <c>kid</c> the
/// set holds → <see cref="SignatureVerificationException"/> (forged or corrupted);
/// <see cref="Crypto.Ed25519.UnpublishedAccountKeyId"/> → <see cref="UnpublishedSigningKeyException"/>;
/// any other <c>kid</c> → <see cref="UnknownSigningKeyException"/> (refresh the set); a
/// <c>kid</c> that cannot be read → <see cref="SignatureVerificationException"/>. Trying every key
/// does not blur "stale set" and "forgery", because the <c>kid</c> still decides the label — it
/// simply no longer gates which key may verify, so a file whose claim and signature disagree but
/// whose signer IS trusted is accepted rather than refused. Nothing read from an unverified
/// payload ever selects a key; it can only choose between failure messages.
/// </para>
/// <para>
/// <b>Ed25519 only.</b> Every key the server publishes is Ed25519 (rotation is
/// <c>rotate_ed25519</c>), and <c>.lic</c> files are Ed25519-signed regardless of the license's own
/// <see cref="LicenseScheme"/>. A <c>.machine</c> file signed under an RSA or ECDSA scheme cannot
/// be verified through this path at all — see
/// <see cref="SigningKeyNotApplicableException"/>.
/// </para>
/// <para>
/// <b>An empty set is not an error, but it is no longer the norm.</b> An account created before
/// the server's key-set backfill may still answer <c>{"data": []}</c> from a server that has not
/// run its startup sweep; after it, every account publishes its active key from creation. Pin the
/// account's published key with <see cref="SigningKey.FromEd25519PublicKey"/> and verification
/// works before the backfill as well as after it.
/// </para>
/// </remarks>
public sealed class SigningKeySet
{
    private readonly IReadOnlyList<SigningKey> _keys;

    /// <summary>A set holding no keys. Every verification through it reports <see cref="UnknownSigningKeyException"/>.</summary>
    public static SigningKeySet Empty { get; } = new(Array.Empty<SigningKey>());

    /// <summary>
    /// Builds a set from keys the caller already holds — pinned constants, a bundled file, or a
    /// fetch made with a credential an embedded client does not have.
    /// </summary>
    /// <remarks>
    /// Keys whose <see cref="SigningKey.Algorithm"/> is not Ed25519, or whose
    /// <see cref="SigningKey.PublicKey"/> does not decode to 32 bytes, are kept in
    /// <see cref="Keys"/> but never returned by <see cref="Find"/> — see <see cref="UsableKeys"/>.
    /// </remarks>
    /// <param name="keys">The keys to index.</param>
    public SigningKeySet(IEnumerable<SigningKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _keys = keys.ToList();
    }

    /// <summary>
    /// Builds a set from public keys pinned by the caller, each standard base64 of the raw 32
    /// bytes, deriving each <c>kid</c> locally so this works with no network access at all.
    /// </summary>
    /// <remarks>
    /// <b>Strict on purpose</b>, unlike <see cref="FromResources"/>: a key that is not valid base64
    /// of exactly 32 bytes throws rather than being skipped. A typo in a key pinned in an
    /// application binary must fail loudly at startup, not silently produce a set that reports
    /// every genuine file in the field as signed by an unknown key.
    /// </remarks>
    /// <param name="publicKeys">The base64 public keys to pin.</param>
    /// <exception cref="ArgumentException">A key is not valid base64 of exactly 32 bytes.</exception>
    public static SigningKeySet FromPublicKeys(params string[] publicKeys)
        => FromPublicKeys((IEnumerable<string>)publicKeys);

    /// <inheritdoc cref="FromPublicKeys(string[])"/>
    public static SigningKeySet FromPublicKeys(IEnumerable<string> publicKeys)
    {
        ArgumentNullException.ThrowIfNull(publicKeys);

        var keys = new List<SigningKey>();
        foreach (var publicKey in publicKeys)
        {
            if (publicKey is null)
            {
                throw new ArgumentException("A pinned signing key was null.", nameof(publicKeys));
            }

            var key = SigningKey.FromEd25519PublicKey(publicKey);
            if (!key.TryGetPublicKeyBytes(out _))
            {
                throw new ArgumentException(
                    $"Pinned signing key '{publicKey}' is not valid base64 of exactly {SigningKey.Ed25519PublicKeyLength} bytes. " +
                    "A mistyped pinned key must fail at startup — skipping it would report every genuine file as signed by an unknown key.",
                    nameof(publicKeys));
            }

            keys.Add(key);
        }

        return new SigningKeySet(keys);
    }

    /// <summary>
    /// Builds a set from the account's published key set, as returned by
    /// <c>TamgaClient.ListSigningKeysAsync</c>.
    /// </summary>
    /// <remarks>
    /// <b>Lenient where <see cref="FromPublicKeys(string[])"/> is strict, and for the opposite reason:</b>
    /// this input is the server's whole key history, and one unusable row — a future non-Ed25519
    /// algorithm, a legacy key that does not decode — must not strand every file the account has
    /// already signed. Such rows are retained in <see cref="Keys"/> for diagnostics but excluded
    /// from <see cref="UsableKeys"/> and never matched by <see cref="Find"/>; a file naming one
    /// surfaces as <see cref="UnknownSigningKeyException"/> with the <c>kid</c> in hand.
    ///
    /// The <c>kid</c> is taken from each resource's <c>id</c>, which <em>is</em> the <c>kid</c>, so
    /// no local hashing happens on this path. <see cref="InconsistentKeys"/> reports any row whose
    /// served id disagrees with the locally computed one.
    /// </remarks>
    /// <param name="resources">The JSON:API resources from the key-set endpoint.</param>
    public static SigningKeySet FromResources(IEnumerable<SigningKeyResource> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        return new SigningKeySet(resources.Select(SigningKey.FromResource));
    }

    /// <summary>Every key the set was built from, usable or not, in the order supplied.</summary>
    public IReadOnlyList<SigningKey> Keys => _keys;

    /// <summary>
    /// The keys that can actually verify something: Ed25519, with a public key that decodes to 32
    /// bytes, and a non-empty id.
    /// </summary>
    /// <remarks>
    /// Compare against <see cref="Keys"/> when you need to know that something was dropped — a set
    /// that fetched ten rows and can use none of them is a very different situation from an empty
    /// account, and both are silent otherwise.
    /// </remarks>
    public IReadOnlyList<SigningKey> UsableKeys => _keys.Where(IsUsable).ToList();

    /// <summary>
    /// Keys whose server-served id disagrees with the id computed locally from their public key.
    /// </summary>
    /// <remarks>
    /// Always empty against a correct server. Non-empty means either the server labelled a key
    /// inconsistently or this SDK's <see cref="Crypto.Ed25519.KeyId(string)"/> has drifted from
    /// <c>shared/crypto/license_file.rs</c> — worth reporting upstream either way. It deliberately
    /// does not fail the fetch: lookup uses the served id, which is the side the file's claim was
    /// written from, so a mismatch does not stop a genuine file verifying.
    /// </remarks>
    public IReadOnlyList<SigningKey> InconsistentKeys =>
        _keys.Where(k => IsUsable(k) && !k.KeyIdIsSelfConsistent).ToList();

    /// <summary>The <c>kid</c>s this set can verify against. Useful in a log line beside a failure.</summary>
    public IReadOnlyList<string> KeyIds => UsableKeys.Select(k => k.KeyId).ToList();

    /// <summary>How many usable keys the set holds.</summary>
    public int Count => UsableKeys.Count;

    /// <summary>Whether the set holds no usable key at all.</summary>
    /// <remarks>Not an error in itself — every verification through such a set reports <see cref="UnknownSigningKeyException"/> (or <see cref="SignatureVerificationException"/> when the <c>kid</c> is unreadable), which is the honest answer — but it is almost always a sign that the fetch or the pinned key list is wrong: after the server's key-set backfill every account publishes at least its active key. See the type-level note.</remarks>
    public bool IsEmpty => Count == 0;

    /// <summary>
    /// The usable key this set holds under <paramref name="keyId"/>, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Matching is exact and case-sensitive: the server emits lowercase hex on both sides, in the
    /// resource <c>id</c> and in the file's claim alike. Matching on the <em>served</em> id and not
    /// on the locally computed one is deliberate — the server writes a file's claim from the same
    /// value it serves as the resource id, so that is the authoritative side.
    /// </remarks>
    /// <param name="keyId">The <c>kid</c> to look up.</param>
    public SigningKey? Find(string keyId)
    {
        if (string.IsNullOrEmpty(keyId))
        {
            return null;
        }

        foreach (var key in _keys)
        {
            if (IsUsable(key) && string.Equals(key.KeyId, keyId, StringComparison.Ordinal))
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>
    /// The first usable key for which <paramref name="verifiesWith"/> answers
    /// <see langword="true"/> when handed its raw 32 public-key bytes, or <see langword="null"/>
    /// when none does. Keys are tried in the order supplied; unusable rows are skipped.
    /// </summary>
    internal SigningKey? FindVerifyingKey(Func<byte[], bool> verifiesWith)
    {
        foreach (var key in _keys)
        {
            if (IsUsable(key) && key.TryGetPublicKeyBytes(out var publicKey) && verifiesWith(publicKey))
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>
    /// The exception to throw when no key in this set verified a file's signature, chosen by the
    /// <c>kid</c> the file claims — the condition that distinguishes a stale key set from a forged
    /// file.
    /// </summary>
    /// <param name="claimedKeyId">The <c>kid</c> read from the (unverified) payload, or <see langword="null"/> when it could not be read.</param>
    /// <param name="fileKind">"License file" or "Machine file", for the message.</param>
    /// <returns>
    /// <see cref="SignatureVerificationException"/> when the <c>kid</c> is unreadable or names a key
    /// this set holds (the file is forged or corrupted);
    /// <see cref="UnpublishedSigningKeyException"/> when it is
    /// <see cref="Crypto.Ed25519.UnpublishedAccountKeyId"/>; otherwise
    /// <see cref="UnknownSigningKeyException"/>.
    /// </returns>
    internal Exception UnverifiableFileFailure(string? claimedKeyId, string fileKind)
    {
        if (string.IsNullOrEmpty(claimedKeyId))
        {
            return new SignatureVerificationException(
                $"{fileKind} signature verification failed against every usable key in the supplied key set " +
                $"({DescribeHeldKeys()}), and its 'kid' claim could not be read to say which key it expected — " +
                "the file is forged or corrupted, or (if encrypted) the license key is wrong as well.");
        }

        if (Find(claimedKeyId) is not null)
        {
            return new SignatureVerificationException(
                $"{fileKind} signature verification failed against the key its 'kid' claim names ('{claimedKeyId}'), " +
                "which IS in the supplied key set, and against every other key the set holds — the file is forged or corrupted.");
        }

        // Checked after the lookup, not before: if a set somehow does hold this id, honouring it
        // beats diagnosing it. In practice no set can — the empty string is not a valid key, so
        // IsUsable rejects it — but the ordering costs nothing and removes the special case.
        if (string.Equals(claimedKeyId, Crypto.Ed25519.UnpublishedAccountKeyId, StringComparison.Ordinal))
        {
            return new UnpublishedSigningKeyException(claimedKeyId, KeyIds);
        }

        return new UnknownSigningKeyException(claimedKeyId, KeyIds);
    }

    private string DescribeHeldKeys() => Count == 0 ? "the set held no usable key" : $"had: {string.Join(", ", KeyIds)}";

    private static bool IsUsable(SigningKey key) =>
        !string.IsNullOrEmpty(key.KeyId) && key.IsEd25519 && key.TryGetPublicKeyBytes(out _);
}
