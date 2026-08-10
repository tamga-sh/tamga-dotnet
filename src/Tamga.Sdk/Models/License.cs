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
    [JsonPropertyName("product")]
    public Guid? Product { get; init; }

    [JsonPropertyName("policy")]
    public Guid? Policy { get; init; }

    [JsonPropertyName("user")]
    public Guid? User { get; init; }

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
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    [JsonPropertyName("suspended")]
    public bool Suspended { get; init; }

    [JsonPropertyName("expiry")]
    public DateTimeOffset? Expiry { get; init; }

    [JsonPropertyName("uses")]
    public int Uses { get; init; }

    [JsonPropertyName("last_validated_at")]
    public DateTimeOffset? LastValidatedAt { get; init; }

    [JsonPropertyName("last_check_in_at")]
    public DateTimeOffset? LastCheckInAt { get; init; }

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
    public Guid Id { get; init; }
    public string? Key { get; init; }
    public bool Suspended { get; init; }
    public DateTimeOffset? Expiry { get; init; }
    public int Uses { get; init; }
    public DateTimeOffset? LastValidatedAt { get; init; }
    public DateTimeOffset? LastCheckInAt { get; init; }
    public Guid? ProductId { get; init; }
    public Guid? PolicyId { get; init; }
    public Guid? UserId { get; init; }
    public Guid? EnvironmentId { get; init; }
    public Dictionary<string, JsonElement>? Metadata { get; init; }

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

/// <summary>The <c>{"data": &lt;LicenseResource&gt;}</c> payload embedded in a plain (unencrypted) <c>.lic</c> file.</summary>
public sealed record LicenseFilePayload
{
    [JsonPropertyName("data")]
    public required JsonApiResource<LicenseAttributes> Data { get; init; }
}

/// <summary>
/// The <c>meta</c> object on the validate-by-key and validate-by-ID responses:
/// <c>{ ts, valid, detail, code }</c>.
/// </summary>
public sealed record ValidationMeta
{
    [JsonPropertyName("ts")]
    public DateTimeOffset Ts { get; init; }

    [JsonPropertyName("valid")]
    public bool Valid { get; init; }

    [JsonPropertyName("detail")]
    public string Detail { get; init; } = "";

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
    public required License License { get; init; }
    public required ValidationMeta Meta { get; init; }

    public bool Valid => Meta.Valid;
    public ValidationCode Code => Meta.Code;
    public string Detail => Meta.Detail;
}

/// <summary>
/// Result of <see cref="TamgaClient.QuickValidateAsync"/> — the quick-validate endpoint's flat,
/// non-JSON:API-enveloped <c>{ ts, valid, detail, code }</c> body (no <c>data</c> key, no license
/// resource).
/// </summary>
public sealed record QuickValidationResult
{
    [JsonPropertyName("ts")]
    public DateTimeOffset Ts { get; init; }

    [JsonPropertyName("valid")]
    public bool Valid { get; init; }

    [JsonPropertyName("detail")]
    public string Detail { get; init; } = "";

    [JsonPropertyName("code")]
    [JsonConverter(typeof(ValidationCodeConverter))]
    public ValidationCode Code { get; init; }
}

/// <summary>Request body for <c>POST /licenses/actions/validate-key</c>: <c>{ "key": "..." }</c>, no scope support.</summary>
public sealed record ValidateByKeyRequest
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }
}

/// <summary>The <c>meta</c> object sent on <c>POST /licenses/{id}/actions/validate</c>.</summary>
public sealed record ValidateByIdRequestMeta
{
    [JsonPropertyName("scope")]
    public Scope? Scope { get; init; }

    [JsonPropertyName("skip_touch")]
    public bool SkipTouch { get; init; }
}

/// <summary>Request body for <c>POST /licenses/{id}/actions/validate</c>: <c>{ "meta": {...} }</c>, body optional.</summary>
public sealed record ValidateByIdRequest
{
    [JsonPropertyName("meta")]
    public required ValidateByIdRequestMeta Meta { get; init; }
}
