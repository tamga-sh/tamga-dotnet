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
    /// <summary>The entitlement's display name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>The stable, developer-facing identifier — match on this, NOT <see cref="Name"/> (a display label that can change).</summary>
    [JsonPropertyName("code")]
    public string Code { get; init; } = "";

    /// <summary>Arbitrary key/value metadata attached to the entitlement.</summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }

    /// <summary>When the entitlement was created.</summary>
    [JsonPropertyName("created")]
    public DateTimeOffset? Created { get; init; }

    /// <summary>When the entitlement was last updated.</summary>
    [JsonPropertyName("updated")]
    public DateTimeOffset? Updated { get; init; }
}

/// <summary>An entitlement resource, flattened from the JSON:API shape like <see cref="License"/>/<see cref="Machine"/>.</summary>
public sealed record Entitlement
{
    /// <summary>The entitlement's unique identifier.</summary>
    public Guid Id { get; init; }

    /// <summary>The entitlement's display name.</summary>
    public string Name { get; init; } = "";

    /// <summary>The stable, developer-facing identifier — match on this, NOT <see cref="Name"/> (a display label that can change).</summary>
    public string Code { get; init; } = "";

    /// <summary>Arbitrary key/value metadata attached to the entitlement.</summary>
    public Dictionary<string, JsonElement>? Metadata { get; init; }

    /// <summary>When the entitlement was created.</summary>
    public DateTimeOffset? Created { get; init; }

    /// <summary>When the entitlement was last updated.</summary>
    public DateTimeOffset? Updated { get; init; }

    /// <summary>Flattens a raw JSON:API entitlement resource into an <see cref="Entitlement"/>.</summary>
    /// <param name="resource">The JSON:API resource, with <c>data.id</c> and <c>data.attributes</c>.</param>
    /// <returns>The flattened <see cref="Entitlement"/>.</returns>
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
