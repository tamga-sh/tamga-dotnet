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

    /// <summary>
    /// <see langword="true"/> when the license holds this entitlement through its policy rather
    /// than by a direct attachment.
    /// </summary>
    /// <remarks>
    /// Only <c>GET /licenses/{id}/entitlements</c> emits this; it is absent (and so
    /// <see langword="null"/>) on account-, policy- and release-scoped entitlement responses.
    ///
    /// An inherited entitlement behaves differently on every write path: attaching it again
    /// answers <c>422 ENTITLEMENT_ALREADY_INHERITED</c>, detaching it answers
    /// <c>403 POLICY_ENTITLEMENT</c>, and fetching it by id answers <c>404</c> (see
    /// <see cref="TamgaClient.GetEntitlementAsync"/>). It still counts for
    /// <see cref="Scope.Entitlements"/>.
    /// </remarks>
    [JsonPropertyName("inherited")]
    public bool? Inherited { get; init; }

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

    /// <summary>
    /// <see langword="true"/> when the license holds this through its policy rather than directly;
    /// <see langword="null"/> on responses that do not carry the flag. See
    /// <see cref="EntitlementAttributes.Inherited"/> for what it changes.
    /// </summary>
    public bool? Inherited { get; init; }

    /// <summary>Arbitrary key/value metadata attached to the entitlement.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; init; }

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
            Inherited = attrs.Inherited,
            Metadata = attrs.Metadata,
            Created = attrs.Created,
            Updated = attrs.Updated,
        };
    }
}
