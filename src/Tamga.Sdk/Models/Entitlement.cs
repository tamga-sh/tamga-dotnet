using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamga.Sdk.Models;

/// <summary>
/// A full entitlement resource. Despite being nested under <c>/licenses/{id}/entitlements</c> in
/// the URL, these are complete <see cref="Entitlement"/> resources, not lightweight
/// junction/relationship records.
/// </summary>
public sealed record EntitlementAttributes
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>The stable, developer-facing identifier — match on this, NOT <see cref="Name"/> (a display label that can change).</summary>
    [JsonPropertyName("code")]
    public string Code { get; init; } = "";

    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }

    [JsonPropertyName("created")]
    public DateTimeOffset? Created { get; init; }

    [JsonPropertyName("updated")]
    public DateTimeOffset? Updated { get; init; }
}

/// <summary>An entitlement resource, flattened from the JSON:API shape like <see cref="License"/>/<see cref="Machine"/>.</summary>
public sealed record Entitlement
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string Code { get; init; } = "";
    public Dictionary<string, JsonElement>? Metadata { get; init; }
    public DateTimeOffset? Created { get; init; }
    public DateTimeOffset? Updated { get; init; }

    public static Entitlement FromResource(JsonApiResource<EntitlementAttributes> resource)
    {
        var attrs = resource.Attributes ?? new EntitlementAttributes();
        return new Entitlement
        {
            Id = resource.Id,
            Name = attrs.Name,
            Code = attrs.Code,
            Metadata = attrs.Metadata,
            Created = attrs.Created,
            Updated = attrs.Updated,
        };
    }
}
