using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamga.Sdk.Models;

/// <summary>
/// <c>meta.scope</c> on the validate-by-ID endpoint. All 8 fields are optional; unset fields are
/// omitted on serialize (see <see cref="TamgaJsonOptions"/>).
/// </summary>
/// <remarks>
/// GOTCHA: only <see cref="Product"/>/<see cref="Policy"/>/<see cref="User"/>/
/// <see cref="Environment"/> are actually enforced server-side today.
/// <see cref="Entitlements"/>/<see cref="Fingerprint"/>/<see cref="Version"/>/<see cref="Checksum"/>
/// are parsed but silently ignored — modeled here for forward-compatibility, not because they
/// currently constrain anything.
/// </remarks>
public sealed record Scope
{
    /// <summary>Restricts validation to this product ID. Enforced server-side — see type-level remarks.</summary>
    [JsonPropertyName("product")]
    public Guid? Product { get; init; }

    /// <summary>Restricts validation to this policy ID. Enforced server-side — see type-level remarks.</summary>
    [JsonPropertyName("policy")]
    public Guid? Policy { get; init; }

    /// <summary>Restricts validation to this user ID. Enforced server-side — see type-level remarks.</summary>
    [JsonPropertyName("user")]
    public Guid? User { get; init; }

    /// <summary>Restricts validation to this environment ID. Enforced server-side — see type-level remarks.</summary>
    [JsonPropertyName("environment")]
    public Guid? Environment { get; init; }

    /// <summary>Parsed but silently ignored server-side today — see type-level remarks.</summary>
    [JsonPropertyName("entitlements")]
    public string[]? Entitlements { get; init; }

    /// <summary>Parsed but silently ignored server-side today — see type-level remarks.</summary>
    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; init; }

    /// <summary>Parsed but silently ignored server-side today — see type-level remarks.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>Parsed but silently ignored server-side today — see type-level remarks.</summary>
    [JsonPropertyName("checksum")]
    public string? Checksum { get; init; }
}

/// <summary>The JSON:API <c>attributes</c> bag for a license resource.</summary>
public sealed record LicenseAttributes
{
    /// <summary>The license key string.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>Whether the license has been manually suspended.</summary>
    [JsonPropertyName("suspended")]
    public bool Suspended { get; init; }

    /// <summary>The license's expiration timestamp, if any.</summary>
    [JsonPropertyName("expiry")]
    public DateTimeOffset? Expiry { get; init; }

    /// <summary>The number of times the license has been used.</summary>
    [JsonPropertyName("uses")]
    public int Uses { get; init; }

    /// <summary>Timestamp of the license's last successful validation, if any.</summary>
    [JsonPropertyName("last_validated_at")]
    public DateTimeOffset? LastValidatedAt { get; init; }

    /// <summary>Timestamp of the license's last check-in, if any.</summary>
    [JsonPropertyName("last_check_in_at")]
    public DateTimeOffset? LastCheckInAt { get; init; }

    /// <summary>Arbitrary key/value metadata attached to the license.</summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}

/// <summary>
/// A license resource, flattened from the JSON:API <c>data.attributes</c> + <c>data.id</c> +
/// <c>data.relationships</c> shape for ergonomic use. See <see cref="JsonApiResource{TAttributes}"/>
/// for the raw wire shape this is built from.
/// </summary>
public sealed record License
{
    /// <summary>The license's unique identifier.</summary>
    public Guid Id { get; init; }

    /// <summary>The license key string.</summary>
    public string? Key { get; init; }

    /// <summary>Whether the license has been manually suspended.</summary>
    public bool Suspended { get; init; }

    /// <summary>The license's expiration timestamp, if any.</summary>
    public DateTimeOffset? Expiry { get; init; }

    /// <summary>The number of times the license has been used.</summary>
    public int Uses { get; init; }

    /// <summary>Timestamp of the license's last successful validation, if any.</summary>
    public DateTimeOffset? LastValidatedAt { get; init; }

    /// <summary>Timestamp of the license's last check-in, if any.</summary>
    public DateTimeOffset? LastCheckInAt { get; init; }

    /// <summary>The ID of the product this license belongs to, if any.</summary>
    public Guid? ProductId { get; init; }

    /// <summary>The ID of the policy this license belongs to, if any.</summary>
    public Guid? PolicyId { get; init; }

    /// <summary>The ID of the user this license is assigned to, if any.</summary>
    public Guid? UserId { get; init; }

    /// <summary>The ID of the environment this license belongs to, if any.</summary>
    public Guid? EnvironmentId { get; init; }

    /// <summary>Arbitrary key/value metadata attached to the license.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; init; }

    /// <summary>
    /// Flattens a raw JSON:API license resource (<c>data.id</c> + <c>data.attributes</c> +
    /// <c>data.relationships</c>) into a <see cref="License"/>. Shared by
    /// <see cref="TamgaClient"/>'s response mapping and <see cref="Checkout.LicenseFile"/>'s
    /// embedded-payload parsing, so both paths produce an identically-shaped result.
    /// </summary>
    public static License FromResource(JsonApiResource<LicenseAttributes> resource)
    {
        var attrs = resource.Attributes ?? new LicenseAttributes();
        return new License
        {
            Id = resource.Id,
            Key = attrs.Key,
            Suspended = attrs.Suspended,
            Expiry = attrs.Expiry,
            Uses = attrs.Uses,
            LastValidatedAt = attrs.LastValidatedAt,
            LastCheckInAt = attrs.LastCheckInAt,
            ProductId = RelationshipId(resource, "product"),
            PolicyId = RelationshipId(resource, "policy"),
            UserId = RelationshipId(resource, "user"),
            EnvironmentId = RelationshipId(resource, "environment"),
            Metadata = attrs.Metadata,
        };
    }

    private static Guid? RelationshipId(JsonApiResource<LicenseAttributes> resource, string name) =>
        resource.Relationships is { } rels && rels.TryGetValue(name, out var rel) ? rel.Data?.Id : null;
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
