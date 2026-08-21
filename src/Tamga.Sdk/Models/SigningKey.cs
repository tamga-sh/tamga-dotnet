using System.Text.Json.Serialization;
using Tamga.Sdk.Crypto;

namespace Tamga.Sdk.Models;

/// <summary>
/// The JSON:API <c>attributes</c> bag of a <c>signing-keys</c> resource:
/// <c>{ algorithm, publicKey, status, created, retired }</c>.
/// </summary>
/// <remarks>
/// GOTCHA: <c>publicKey</c> is camelCase — an explicit <c>#[serde(rename)]</c> server-side
/// (<c>accounts/serializer.rs:111-112</c>) in an otherwise lowercase bag. Do not generalise the
/// casing to its neighbours; <c>algorithm</c>/<c>status</c>/<c>created</c>/<c>retired</c> are
/// plain.
///
/// <c>retired</c> is <em>absent</em> rather than null while a key is active — the server marks it
/// <c>skip_serializing_if = "Option::is_none"</c> (<c>serializer.rs:115-116</c>).
///
/// Every property is nullable even though the server declares each non-optional, so one unexpected
/// omission degrades to <see langword="null"/> on one key rather than failing the whole list
/// decode and stranding an account's entire key history.
/// </remarks>
public sealed record SigningKeyAttributes
{
    /// <summary>The signing algorithm — <c>ed25519</c> on every row the server writes today.</summary>
    [JsonPropertyName("algorithm")]
    public string? Algorithm { get; init; }

    /// <summary>The public key, standard base64 of the raw key bytes. Note the camelCase wire name.</summary>
    [JsonPropertyName("publicKey")]
    public string? PublicKey { get; init; }

    /// <summary><c>active</c> or <c>retired</c>.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>When the key was created.</summary>
    [JsonPropertyName("created")]
    public DateTimeOffset? Created { get; init; }

    /// <summary>When the key was retired, or <see langword="null"/> while it is active.</summary>
    [JsonPropertyName("retired")]
    public DateTimeOffset? Retired { get; init; }
}

/// <summary>
/// A JSON:API <c>signing-keys</c> resource: <c>{ type, id, attributes }</c>, where <c>id</c> is the
/// <c>kid</c> itself.
/// </summary>
/// <remarks>
/// CRITICAL — this cannot reuse <see cref="JsonApiResource{TAttributes}"/>, and the reason is a
/// hard type mismatch rather than a style preference: that type declares <c>id</c> as a
/// <see cref="Guid"/>, and a <c>kid</c> is sixteen hex characters
/// (<c>"51643eac9777b63a"</c>). <see cref="Guid"/> cannot parse sixteen hex characters, so decoding
/// a key set through the shared envelope throws <see cref="System.Text.Json.JsonException"/> on
/// every response the server can produce.
///
/// Note also that <c>id</c> is a sibling of <c>attributes</c>, not a member of it. Decoding
/// straight into a flat model — skipping the envelope — is a defect this repo has actually shipped
/// (the component/process routes), and it is silent: with no
/// <c>UnmappedMemberHandling</c> configured, the unknown <c>data</c> key is ignored and the caller
/// gets a well-formed object full of empty strings.
/// </remarks>
public sealed record SigningKeyResource
{
    /// <summary>The resource type discriminator — always <c>"signing-keys"</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    /// <summary>
    /// The resource id, which <b>is</b> the <c>kid</c> an offline file's claim names — the server
    /// sets <c>id: k.kid</c> from the same value it writes into the file
    /// (<c>accounts/serializer.rs:122-123</c>).
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    /// <summary>The resource's attributes payload.</summary>
    [JsonPropertyName("attributes")]
    public SigningKeyAttributes? Attributes { get; init; }
}

/// <summary>
/// The JSON:API envelope for <c>GET /accounts/{account_id}/signing-keys</c>:
/// <c>{ "data": [ { type, id, attributes }, ... ] }</c>.
/// </summary>
/// <remarks>
/// A separate envelope from <see cref="JsonApiListDocument{TAttributes}"/> for the same reason
/// <see cref="SigningKeyResource"/> is separate from <see cref="JsonApiResource{TAttributes}"/> —
/// the string <c>id</c>. See that type's remarks.
/// </remarks>
public sealed record SigningKeyListDocument
{
    /// <summary>The account's published keys, newest first.</summary>
    [JsonPropertyName("data")]
    public IReadOnlyList<SigningKeyResource> Data { get; init; } = Array.Empty<SigningKeyResource>();

    /// <summary>The errors returned instead of <see cref="Data"/> when the request failed.</summary>
    [JsonPropertyName("errors")]
    public IReadOnlyList<TamgaApiError>? Errors { get; init; }
}

/// <summary>
/// One public signing key an account has held — current or retired — as published by
/// <c>GET /accounts/{account_id}/signing-keys</c>.
/// </summary>
/// <remarks>
/// <para>
/// The point of this type is key rotation. An offline <c>.lic</c> or <c>.machine</c> file carries a
/// <c>kid</c> claim inside its signed bytes naming the key that signed it, and the account keeps
/// every key it has ever used (<c>account_signing_keys</c>). Without the key set, a file signed
/// before a rotation fails against the current key and reports the <em>same</em>
/// <see cref="SignatureVerificationException"/> a forged file does — so a paying customer holding a
/// perfectly authentic file is indistinguishable from an attacker. With it, the two are separate
/// conditions. See <see cref="Checkout.SigningKeySet"/>.
/// </para>
/// <para>
/// <b>A key set does not have to come from the network</b>, and for most embedded clients it
/// cannot: see the permission note on <c>TamgaClient.ListSigningKeysAsync</c>. Build keys with
/// <see cref="FromEd25519PublicKey"/> to pin them at build time, and verification works with no
/// network at all.
/// </para>
/// </remarks>
public sealed record SigningKey
{
    /// <summary>
    /// The <c>algorithm</c> value the server writes for every published key today.
    /// </summary>
    /// <remarks>
    /// The table's <c>CHECK</c> also permits <c>rsa2048</c> and <c>ecdsa_p256</c>, but
    /// <c>rotate_ed25519</c> is the only code path that writes a row and it hardcodes
    /// <c>'ed25519'</c> in both of its <c>INSERT</c>s (<c>signing_keys.rs:95-99,150-154</c>). So the
    /// published set is Ed25519-only in practice, and key selection filters on this value rather
    /// than assuming it.
    /// </remarks>
    public const string Ed25519Algorithm = "ed25519";

    /// <summary>Wire <c>status</c> for the key currently signing new files. At most one per algorithm.</summary>
    public const string ActiveStatus = "active";

    /// <summary>Wire <c>status</c> for a key kept for verification only.</summary>
    public const string RetiredStatus = "retired";

    /// <summary>The raw Ed25519 public key length, in bytes.</summary>
    public const int Ed25519PublicKeyLength = 32;

    /// <summary>
    /// The key's id — the JSON:API resource <c>id</c>, and the value an offline file's <c>kid</c>
    /// claim names.
    /// </summary>
    /// <remarks>
    /// Taken from the served <c>id</c>, not computed, on anything that came off the wire. The
    /// server derives it from <see cref="PublicKey"/> by the rule
    /// <see cref="Ed25519.KeyId(string)"/> implements, so it is checkable rather than merely
    /// trusted — see <see cref="KeyIdIsSelfConsistent"/>.
    /// </remarks>
    public required string KeyId { get; init; }

    /// <summary>The signing algorithm, e.g. <see cref="Ed25519Algorithm"/>.</summary>
    public string Algorithm { get; init; } = Ed25519Algorithm;

    /// <summary>
    /// The public key <b>exactly as the server publishes it</b>: standard base64 of the raw key
    /// bytes.
    /// </summary>
    /// <remarks>
    /// Kept as the published string rather than as decoded bytes because <b>the string is what the
    /// <c>kid</c> hashes</b>. Normalising it — re-encoding, trimming, converting to PEM — changes
    /// the hash and silently breaks every match. Use <see cref="TryGetPublicKeyBytes"/> for the
    /// decoded form.
    /// </remarks>
    public required string PublicKey { get; init; }

    /// <summary>
    /// <see cref="ActiveStatus"/> or <see cref="RetiredStatus"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately a <see cref="string"/> and not an enum. The column's <c>CHECK</c> admits
    /// exactly those two today, but this fleet has been bitten by closed enums over wire values
    /// before, and an unknown future status must not fail a whole decode.
    /// </remarks>
    public string Status { get; init; } = ActiveStatus;

    /// <summary>When the key was created.</summary>
    public DateTimeOffset? Created { get; init; }

    /// <summary>When the key was retired, or <see langword="null"/> while it is active.</summary>
    public DateTimeOffset? Retired { get; init; }

    /// <summary>
    /// This key's id as computed locally from <see cref="PublicKey"/>, independently of what the
    /// server served.
    /// </summary>
    public string ComputedKeyId => Ed25519.KeyId(PublicKey);

    /// <summary>
    /// Whether the server-published <see cref="KeyId"/> matches the one derived from
    /// <see cref="PublicKey"/>.
    /// </summary>
    /// <remarks>
    /// Key lookup matches on the <em>served</em> id (see <see cref="Checkout.SigningKeySet.Find"/>) — the
    /// server sets the resource id from the same value it writes into a file's claim, so that is
    /// the authoritative side and the local computation is a cross-check. A
    /// <see langword="false"/> here is worth reporting upstream; it is not something a client can
    /// fix, and <see cref="Checkout.SigningKeySet.InconsistentKeys"/> surfaces it without failing the fetch.
    /// </remarks>
    public bool KeyIdIsSelfConsistent =>
        string.Equals(ComputedKeyId, KeyId, StringComparison.Ordinal);

    /// <summary>Whether this key is retired — kept for verification, no longer signing.</summary>
    public bool IsRetired => string.Equals(Status, RetiredStatus, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this key's <see cref="Algorithm"/> is Ed25519.</summary>
    public bool IsEd25519 => string.Equals(Algorithm, Ed25519Algorithm, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Decodes <see cref="PublicKey"/> into the raw 32 bytes Ed25519 verification needs, returning
    /// <see langword="false"/> rather than throwing when it is not valid base64 of exactly
    /// <see cref="Ed25519PublicKeyLength"/> bytes.
    /// </summary>
    public bool TryGetPublicKeyBytes(out byte[] bytes)
    {
        try
        {
            var decoded = Convert.FromBase64String(PublicKey);
            if (decoded.Length != Ed25519PublicKeyLength)
            {
                bytes = Array.Empty<byte>();
                return false;
            }

            bytes = decoded;
            return true;
        }
        catch (FormatException)
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }

    /// <summary>
    /// Builds an Ed25519 key record from the published public key alone, deriving
    /// <see cref="KeyId"/> locally.
    /// </summary>
    /// <remarks>
    /// The intended way to pin a key into an application binary: a caller who has the public key
    /// does not also need to be told its id, because the id is a function of the key. This is the
    /// path that makes offline verification work with no network access — which matters here more
    /// than usual, since a license-key credential cannot call the key-set endpoint at all.
    /// </remarks>
    /// <param name="publicKey">The public key, standard base64 of the raw 32 bytes.</param>
    /// <param name="status">The key's status; defaults to <see cref="ActiveStatus"/>.</param>
    public static SigningKey FromEd25519PublicKey(string publicKey, string status = ActiveStatus)
    {
        ArgumentNullException.ThrowIfNull(publicKey);

        return new SigningKey
        {
            KeyId = Ed25519.KeyId(publicKey),
            Algorithm = Ed25519Algorithm,
            PublicKey = publicKey,
            Status = status,
        };
    }

    /// <summary>
    /// Maps a JSON:API <c>signing-keys</c> resource onto this model, taking <see cref="KeyId"/>
    /// from the resource <c>id</c> — which <b>is</b> the <c>kid</c>, so nothing is hashed on this
    /// path.
    /// </summary>
    public static SigningKey FromResource(SigningKeyResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var attributes = resource.Attributes;
        return new SigningKey
        {
            KeyId = resource.Id,
            Algorithm = attributes?.Algorithm ?? Ed25519Algorithm,
            PublicKey = attributes?.PublicKey ?? "",
            Status = attributes?.Status ?? ActiveStatus,
            Created = attributes?.Created,
            Retired = attributes?.Retired,
        };
    }
}
