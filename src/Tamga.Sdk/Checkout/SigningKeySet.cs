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
/// <b>How the <c>kid</c> is used, and why that is safe.</b> The <c>kid</c> claim lives
/// <em>inside</em> the signed payload but is read <em>before</em> the signature is checked, which
/// is only sound under one rule: it selects a key from a set the caller already trusts, and can
/// never supply one. A file naming a <c>kid</c> this set does not hold raises
/// <see cref="UnknownSigningKeyException"/>; a file naming one it does hold is verified against
/// exactly that key and nothing else. There is deliberately <b>no "try every key" fallback</b> —
/// trying them all would verify the same set of files while destroying the distinction this type
/// exists for. This is the same discipline JWS <c>kid</c> handling needs.
/// </para>
/// <para>
/// <b>Ed25519 only.</b> Every key the server publishes is Ed25519 (rotation is
/// <c>rotate_ed25519</c>), and <c>.lic</c> files are Ed25519-signed regardless of the license's own
/// <see cref="LicenseScheme"/>. A <c>.machine</c> file signed under an RSA or ECDSA scheme cannot
/// be verified through this path at all — see
/// <see cref="SigningKeyNotApplicableException"/>.
/// </para>
/// <para>
/// <b>An empty set is not an error.</b> <c>account_signing_keys</c> is written only by
/// <c>rotate_ed25519</c>, which backfills the account's current key on its way through
/// (<c>signing_keys.rs:74-107</c>), so an account that has never rotated has no rows at all and the
/// endpoint answers <c>{"data": []}</c>. Pin the account's published key with
/// <see cref="SigningKey.FromEd25519PublicKey"/> and verification works before the first rotation
/// as well as after it.
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
    /// <remarks>
    /// Not an error in itself — every verification through such a set reports
    /// <see cref="UnknownSigningKeyException"/>, which is the honest answer — but it is almost
    /// always a sign that the fetch or the pinned key list is wrong. See the type-level note on why
    /// an empty published set is normal for an account that has never rotated.
    /// </remarks>
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
    /// Resolves the key a file's <c>kid</c> claim names, throwing the condition that distinguishes
    /// a stale key set from a forged file.
    /// </summary>
    /// <param name="keyId">The <c>kid</c> read from the file's signed payload.</param>
    /// <returns>The trusted key to verify against, and its raw 32 public-key bytes.</returns>
    /// <exception cref="UnpublishedSigningKeyException">
    /// <paramref name="keyId"/> is <see cref="Crypto.Ed25519.UnpublishedAccountKeyId"/> — the
    /// server signed with an empty key because the account's key column was never populated.
    /// </exception>
    /// <exception cref="UnknownSigningKeyException">No key in this set has that id.</exception>
    internal (SigningKey Key, byte[] PublicKeyBytes) Resolve(string keyId)
    {
        if (Find(keyId) is { } key && key.TryGetPublicKeyBytes(out var bytes))
        {
            return (key, bytes);
        }

        // Checked after the lookup, not before: if a set somehow does hold this id, honouring it
        // beats diagnosing it. In practice no set can — the empty string is not a valid key, so
        // IsUsable rejects it — but the ordering costs nothing and removes the special case.
        if (string.Equals(keyId, Crypto.Ed25519.UnpublishedAccountKeyId, StringComparison.Ordinal))
        {
            throw new UnpublishedSigningKeyException(keyId, KeyIds);
        }

        throw new UnknownSigningKeyException(keyId, KeyIds);
    }

    private static bool IsUsable(SigningKey key) =>
        !string.IsNullOrEmpty(key.KeyId) && key.IsEd25519 && key.TryGetPublicKeyBytes(out _);
}
