using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamga.Sdk.Models;

/// <summary>
/// <c>meta.scope</c> on the validate-by-ID endpoint. All 8 fields are optional; unset fields are
/// omitted on serialize (see <see cref="TamgaJsonOptions"/>).
/// </summary>
/// <remarks>
/// SIX of the eight fields are enforced server-side and are sent on the wire:
/// <see cref="Product"/>, <see cref="Policy"/>, <see cref="User"/>, <see cref="Environment"/>,
/// <see cref="Entitlements"/> and <see cref="Fingerprint"/>.
///
/// The remaining two — <see cref="Version"/> and <see cref="Checksum"/> — are NOT ignored by the
/// server any more; they are rejected. Sending either one makes the server answer
/// <c>422 SCOPE_NOT_SUPPORTED</c> before any validation runs, so the caller gets no
/// <c>meta.valid</c> at all: the whole validate call fails. Because "silently ignored" and "fails
/// the entire request" are wildly different failure modes for existing callers, this SDK now
/// refuses to put them on the wire — see their per-member remarks. They remain on the type so
/// existing code still compiles; they simply no longer reach the server.
/// </remarks>
public sealed record Scope
{
    /// <summary>Restricts validation to this product ID. Enforced server-side.</summary>
    [JsonPropertyName("product")]
    public Guid? Product { get; init; }

    /// <summary>Restricts validation to this policy ID. Enforced server-side.</summary>
    [JsonPropertyName("policy")]
    public Guid? Policy { get; init; }

    /// <summary>Restricts validation to this user ID. Enforced server-side.</summary>
    [JsonPropertyName("user")]
    public Guid? User { get; init; }

    /// <summary>Restricts validation to this environment ID. Enforced server-side.</summary>
    [JsonPropertyName("environment")]
    public Guid? Environment { get; init; }

    /// <summary>
    /// Entitlement <b>codes</b> the license must hold. ENFORCED — a shortfall answers
    /// <see cref="ValidationCode.EntitlementsMissing"/>.
    /// </summary>
    /// <remarks>
    /// These are <see cref="Entitlement.Code"/> values, NOT the entitlement UUIDs that attach and
    /// detach request bodies take. Comparison is case-insensitive and de-duplicated server-side.
    /// An empty array asserts nothing and always passes. The check is satisfied by the union of
    /// directly-attached and policy-inherited entitlements, so a license can pass on codes it
    /// never had attached to it individually.
    /// </remarks>
    [JsonPropertyName("entitlements")]
    public string[]? Entitlements { get; init; }

    /// <summary>
    /// A machine fingerprint the license must already have activated. ENFORCED — a mismatch
    /// answers <see cref="ValidationCode.FingerprintScopeMismatch"/>.
    /// </summary>
    /// <remarks>
    /// This is the anti-key-sharing control: it matches against ANY machine row on the license,
    /// regardless of that machine's heartbeat status. Pass the same fingerprint the machine was
    /// activated with.
    /// </remarks>
    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; init; }

    /// <summary>
    /// Never sent. Setting this used to be a no-op; on the current server it would fail the entire
    /// validate call with <c>422 SCOPE_NOT_SUPPORTED</c>, so this SDK drops it rather than
    /// degrading a working call into a hard error.
    /// </summary>
    [JsonIgnore]
    [Obsolete("The server rejects scope.version with 422 SCOPE_NOT_SUPPORTED, failing the whole validate call. This SDK no longer sends it; setting it has no effect.")]
    public string? Version { get; init; }

    /// <summary>
    /// Never sent. Setting this used to be a no-op; on the current server it would fail the entire
    /// validate call with <c>422 SCOPE_NOT_SUPPORTED</c>, so this SDK drops it rather than
    /// degrading a working call into a hard error.
    /// </summary>
    [JsonIgnore]
    [Obsolete("The server rejects scope.checksum with 422 SCOPE_NOT_SUPPORTED, failing the whole validate call. This SDK no longer sends it; setting it has no effect.")]
    public string? Checksum { get; init; }
}

/// <summary>The JSON:API <c>attributes</c> bag for a license resource — all 21 attributes the server emits.</summary>
public sealed record LicenseAttributes
{
    /// <summary>The license's display name, if one was set.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>The license key string.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>
    /// The license's derived lifecycle status. Deliberately a plain string, not a closed enum.
    /// </summary>
    /// <remarks>
    /// This is NOT the entitlement decision. Dispatch on <see cref="ValidationMeta.Valid"/> /
    /// <see cref="ValidationMeta.Code"/> from a validate call; this field is a coarse label for
    /// display. Known values include <c>ACTIVE</c>, <c>INACTIVE</c>, <c>EXPIRING</c>,
    /// <c>EXPIRED</c> and <c>SUSPENDED</c> — a plain string so a future addition round-trips
    /// instead of failing to deserialize. Note <c>INACTIVE</c> means "no machines and never
    /// validated", which a client that only ever quick-validates over an <c>Origin</c>-bearing
    /// transport can be stuck in permanently (see <see cref="TamgaClient.QuickValidateAsync"/>).
    /// </remarks>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>The license's expiration timestamp, if any.</summary>
    [JsonPropertyName("expiry")]
    public DateTimeOffset? Expiry { get; init; }

    /// <summary>Whether the license has been manually suspended.</summary>
    [JsonPropertyName("suspended")]
    public bool Suspended { get; init; }

    /// <summary>Whether the license is protected from deletion/modification by non-admin callers.</summary>
    [JsonPropertyName("protected")]
    public bool Protected { get; init; }

    /// <summary>The number of times the license has been used.</summary>
    [JsonPropertyName("uses")]
    public int Uses { get; init; }

    /// <summary>The license's key/checkout signing scheme, if one is set on the license itself.</summary>
    [JsonPropertyName("scheme")]
    public string? Scheme { get; init; }

    /// <summary>Whether the license key is stored encrypted.</summary>
    [JsonPropertyName("encrypted")]
    public bool Encrypted { get; init; }

    /// <summary>Whether the license is in strict mode.</summary>
    [JsonPropertyName("strict")]
    public bool Strict { get; init; }

    /// <summary>Whether the license floats across machines rather than being pinned to one.</summary>
    [JsonPropertyName("floating")]
    public bool Floating { get; init; }

    /// <summary>The license's own machine cap, if set. Applied on top of the policy's cap, under the same overage strategy.</summary>
    [JsonPropertyName("max_machines")]
    public int? MaxMachines { get; init; }

    /// <summary>The license's own use cap, if set.</summary>
    [JsonPropertyName("max_uses")]
    public int? MaxUses { get; init; }

    /// <summary>The license's own user cap, if set.</summary>
    [JsonPropertyName("max_users")]
    public int? MaxUsers { get; init; }

    /// <summary>Timestamp of the license's last successful validation, if any.</summary>
    [JsonPropertyName("last_validated_at")]
    public DateTimeOffset? LastValidatedAt { get; init; }

    /// <summary>Timestamp of the license's last check-in, if any.</summary>
    [JsonPropertyName("last_check_in_at")]
    public DateTimeOffset? LastCheckInAt { get; init; }

    /// <summary>Timestamp of the license's last checkout (offline <c>.lic</c> file issued), if any.</summary>
    [JsonPropertyName("last_check_out_at")]
    public DateTimeOffset? LastCheckOutAt { get; init; }

    /// <summary>How many machines are currently activated against this license.</summary>
    [JsonPropertyName("machines_count")]
    public int MachinesCount { get; init; }

    /// <summary>Arbitrary key/value metadata attached to the license.</summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }

    /// <summary>When the license was created.</summary>
    [JsonPropertyName("created")]
    public DateTimeOffset? Created { get; init; }

    /// <summary>When the license was last updated.</summary>
    [JsonPropertyName("updated")]
    public DateTimeOffset? Updated { get; init; }
}

/// <summary>
/// A license resource, flattened from the JSON:API <c>data.attributes</c> + <c>data.id</c> shape
/// for ergonomic use. See <see cref="JsonApiResource{TAttributes}"/> for the raw wire shape this is
/// built from.
/// </summary>
public sealed record License
{
    /// <summary>The license's unique identifier.</summary>
    public Guid Id { get; init; }

    /// <summary>The license's display name, if one was set.</summary>
    public string? Name { get; init; }

    /// <summary>The license key string.</summary>
    public string? Key { get; init; }

    /// <summary>The license's derived lifecycle status — a display label, not the entitlement decision. See <see cref="LicenseAttributes.Status"/>.</summary>
    public string? Status { get; init; }

    /// <summary>Whether the license has been manually suspended.</summary>
    public bool Suspended { get; init; }

    /// <summary>Whether the license is protected from deletion/modification by non-admin callers.</summary>
    public bool Protected { get; init; }

    /// <summary>The license's expiration timestamp, if any.</summary>
    public DateTimeOffset? Expiry { get; init; }

    /// <summary>The number of times the license has been used.</summary>
    public int Uses { get; init; }

    /// <summary>The license's key/checkout signing scheme, if one is set on the license itself.</summary>
    public string? Scheme { get; init; }

    /// <summary>Whether the license key is stored encrypted.</summary>
    public bool Encrypted { get; init; }

    /// <summary>Whether the license is in strict mode.</summary>
    public bool Strict { get; init; }

    /// <summary>Whether the license floats across machines rather than being pinned to one.</summary>
    public bool Floating { get; init; }

    /// <summary>The license's own machine cap, if set. Applied on top of the policy's cap, under the same overage strategy.</summary>
    public int? MaxMachines { get; init; }

    /// <summary>The license's own use cap, if set.</summary>
    public int? MaxUses { get; init; }

    /// <summary>The license's own user cap, if set.</summary>
    public int? MaxUsers { get; init; }

    /// <summary>Timestamp of the license's last successful validation, if any.</summary>
    public DateTimeOffset? LastValidatedAt { get; init; }

    /// <summary>Timestamp of the license's last check-in, if any.</summary>
    public DateTimeOffset? LastCheckInAt { get; init; }

    /// <summary>Timestamp of the license's last checkout (offline <c>.lic</c> file issued), if any.</summary>
    public DateTimeOffset? LastCheckOutAt { get; init; }

    /// <summary>How many machines are currently activated against this license.</summary>
    public int MachinesCount { get; init; }

    /// <summary>When the license was created.</summary>
    public DateTimeOffset? Created { get; init; }

    /// <summary>When the license was last updated.</summary>
    public DateTimeOffset? Updated { get; init; }

    /// <summary>Always <see langword="null"/>. See the obsolete note.</summary>
    [Obsolete("Always null: the server's license serializer emits only { type, id, attributes } — there is no `relationships` object on a `licenses` resource for this to be read from, and there never was. Use GET /licenses/{id}/product instead. Scheduled for removal in the next minor release.")]
    public Guid? ProductId { get; init; }

    /// <summary>Always <see langword="null"/>. See the obsolete note.</summary>
    [Obsolete("Always null: the server's license serializer emits only { type, id, attributes } — there is no `relationships` object on a `licenses` resource for this to be read from, and there never was. Use GET /licenses/{id}/policy instead. Scheduled for removal in the next minor release.")]
    public Guid? PolicyId { get; init; }

    /// <summary>Always <see langword="null"/>. See the obsolete note.</summary>
    [Obsolete("Always null: the server's license serializer emits only { type, id, attributes } — there is no `relationships` object on a `licenses` resource for this to be read from, and there never was. Use GET /licenses/{id}/owner instead. Scheduled for removal in the next minor release.")]
    public Guid? UserId { get; init; }

    /// <summary>Always <see langword="null"/>. See the obsolete note.</summary>
    [Obsolete("Always null: the server's license serializer emits only { type, id, attributes } — there is no `relationships` object on a `licenses` resource for this to be read from, and there never was. Scheduled for removal in the next minor release.")]
    public Guid? EnvironmentId { get; init; }

    /// <summary>Arbitrary key/value metadata attached to the license.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; init; }

    /// <summary>
    /// Flattens a raw JSON:API license resource (<c>data.id</c> + <c>data.attributes</c>) into a
    /// <see cref="License"/>. Shared by <see cref="TamgaClient"/>'s response mapping and
    /// <see cref="Checkout.LicenseFile"/>'s embedded-payload parsing, so both paths produce an
    /// identically-shaped result.
    /// </summary>
    /// <remarks>
    /// Deliberately does not read <c>data.relationships</c>: the server never emits one on a
    /// license. The four id properties that used to be populated from it
    /// (<c>ProductId</c>/<c>PolicyId</c>/<c>UserId</c>/<c>EnvironmentId</c>) are obsolete and left
    /// unset.
    /// </remarks>
    public static License FromResource(JsonApiResource<LicenseAttributes> resource)
    {
        var attrs = resource.Attributes ?? new LicenseAttributes();
        return new License
        {
            Id = resource.Id,
            Name = attrs.Name,
            Key = attrs.Key,
            Status = attrs.Status,
            Suspended = attrs.Suspended,
            Protected = attrs.Protected,
            Expiry = attrs.Expiry,
            Uses = attrs.Uses,
            Scheme = attrs.Scheme,
            Encrypted = attrs.Encrypted,
            Strict = attrs.Strict,
            Floating = attrs.Floating,
            MaxMachines = attrs.MaxMachines,
            MaxUses = attrs.MaxUses,
            MaxUsers = attrs.MaxUsers,
            LastValidatedAt = attrs.LastValidatedAt,
            LastCheckInAt = attrs.LastCheckInAt,
            LastCheckOutAt = attrs.LastCheckOutAt,
            MachinesCount = attrs.MachinesCount,
            Created = attrs.Created,
            Updated = attrs.Updated,
            Metadata = attrs.Metadata,
        };
    }
}

/// <summary>
/// The <c>{"data": &lt;LicenseResource&gt;, "meta": &lt;claims&gt;}</c> payload embedded in a
/// format-v2 <c>.lic</c> file.
/// </summary>
public sealed record LicenseFilePayload
{
    /// <summary>The wrapped JSON:API license resource.</summary>
    [JsonPropertyName("data")]
    public required JsonApiResource<LicenseAttributes> Data { get; init; }

    /// <summary>
    /// The claims that were covered by the signature. Absent only on a pre-v2 file, which is
    /// rejected.
    /// </summary>
    [JsonPropertyName("meta")]
    public LicenseFileClaims? Meta { get; init; }
}

/// <summary>
/// The claims carried <em>inside</em> the signed bytes of a <c>.lic</c> file.
/// </summary>
/// <remarks>
/// These are the point of format v2. In v1 the <c>ttl</c>/<c>expiry</c> a caller asked for lived
/// only in the JSON:API envelope around the certificate, never inside the signed bytes — so a
/// 24-hour trial file was cryptographically valid forever, because the client is the attacker and
/// any check built on the envelope is bypassed by keeping (or redistributing) the raw certificate
/// string. Unlike the envelope, these cannot be edited by whoever holds the file.
/// </remarks>
public sealed record LicenseFileClaims
{
    /// <summary>Issued-at, seconds since the Unix epoch.</summary>
    [JsonPropertyName("iat")]
    public long IssuedAt { get; init; }

    /// <summary>
    /// Expiry, seconds since the Unix epoch. <c>null</c> means the file never expires — checkout
    /// was made without a <c>ttl</c>.
    /// </summary>
    [JsonPropertyName("exp")]
    public long? ExpiresAt { get; init; }

    /// <summary>Unique per checkout — usable for replay detection.</summary>
    [JsonPropertyName("jti")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Identifies the signing key, so a file survives a key rotation.</summary>
    [JsonPropertyName("kid")]
    public string KeyId { get; init; } = string.Empty;
}

/// <summary>
/// The <c>meta</c> object on the validate-by-key and validate-by-ID responses:
/// <c>{ ts, valid, detail, code }</c>.
/// </summary>
public sealed record ValidationMeta
{
    /// <summary>Timestamp the validation was performed.</summary>
    [JsonPropertyName("ts")]
    public DateTimeOffset Ts { get; init; }

    /// <summary>Whether the license passed validation.</summary>
    [JsonPropertyName("valid")]
    public bool Valid { get; init; }

    /// <summary>Human-readable description of the validation result.</summary>
    [JsonPropertyName("detail")]
    public string Detail { get; init; } = "";

    /// <summary>Machine-readable validation result code.</summary>
    [JsonPropertyName("code")]
    [JsonConverter(typeof(ValidationCodeConverter))]
    public ValidationCode Code { get; init; }
}

/// <summary>
/// Result of <see cref="TamgaClient.ValidateByKeyAsync"/> or <see cref="TamgaClient.ValidateByIdAsync"/>:
/// the JSON:API license resource plus its validation <see cref="ValidationMeta"/>.
/// </summary>
public sealed record ValidationResult
{
    /// <summary>The validated license resource.</summary>
    public required License License { get; init; }

    /// <summary>The validation outcome for the license.</summary>
    public required ValidationMeta Meta { get; init; }

    /// <summary>Shorthand for <see cref="ValidationMeta.Valid"/> on <see cref="Meta"/>.</summary>
    public bool Valid => Meta.Valid;

    /// <summary>Shorthand for <see cref="ValidationMeta.Code"/> on <see cref="Meta"/>.</summary>
    public ValidationCode Code => Meta.Code;

    /// <summary>Shorthand for <see cref="ValidationMeta.Detail"/> on <see cref="Meta"/>.</summary>
    public string Detail => Meta.Detail;
}

/// <summary>
/// Result of <see cref="TamgaClient.QuickValidateAsync"/> — the quick-validate endpoint's flat,
/// non-JSON:API-enveloped <c>{ ts, valid, detail, code }</c> body (no <c>data</c> key, no license
/// resource).
/// </summary>
public sealed record QuickValidationResult
{
    /// <summary>Timestamp the validation was performed.</summary>
    [JsonPropertyName("ts")]
    public DateTimeOffset Ts { get; init; }

    /// <summary>Whether the license passed validation.</summary>
    [JsonPropertyName("valid")]
    public bool Valid { get; init; }

    /// <summary>Human-readable description of the validation result.</summary>
    [JsonPropertyName("detail")]
    public string Detail { get; init; } = "";

    /// <summary>Machine-readable validation result code.</summary>
    [JsonPropertyName("code")]
    [JsonConverter(typeof(ValidationCodeConverter))]
    public ValidationCode Code { get; init; }
}

/// <summary>Request body for <c>POST /licenses/actions/validate-key</c>: <c>{ "key": "..." }</c>, no scope support.</summary>
public sealed record ValidateByKeyRequest
{
    /// <summary>The license key to validate.</summary>
    [JsonPropertyName("key")]
    public required string Key { get; init; }
}

/// <summary>The <c>meta</c> object sent on <c>POST /licenses/{id}/actions/validate</c>.</summary>
public sealed record ValidateByIdRequestMeta
{
    /// <summary>Optional scope constraints to validate the license against.</summary>
    [JsonPropertyName("scope")]
    public Scope? Scope { get; init; }

    /// <summary>When true, skips updating the license's last-validated/check-in timestamps.</summary>
    [JsonPropertyName("skip_touch")]
    public bool SkipTouch { get; init; }
}

/// <summary>Request body for <c>POST /licenses/{id}/actions/validate</c>: <c>{ "meta": {...} }</c>, body optional.</summary>
public sealed record ValidateByIdRequest
{
    /// <summary>The <c>meta</c> payload for the request.</summary>
    [JsonPropertyName("meta")]
    public required ValidateByIdRequestMeta Meta { get; init; }
}
