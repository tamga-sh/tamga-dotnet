using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamga.Sdk.Models;

/// <summary>
/// A machine component resource. <c>POST /components</c> uses a flat, non-JSON:API-enveloped
/// request body (asymmetric vs. <c>POST /machines</c>): <c>{ machine_id, fingerprint, name, metadata }</c>.
/// </summary>
public sealed record Component
{
    /// <summary>The component's unique ID.</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    /// <summary>The ID of the machine this component belongs to.</summary>
    [JsonPropertyName("machine_id")]
    public Guid MachineId { get; init; }

    /// <summary>The component's fingerprint identifier.</summary>
    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; init; } = "";

    /// <summary>The component's display name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>Arbitrary caller-supplied metadata attached to the component.</summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }

    /// <summary>When the component was created.</summary>
    [JsonPropertyName("created")]
    public DateTimeOffset? Created { get; init; }

    /// <summary>When the component was last updated.</summary>
    [JsonPropertyName("updated")]
    public DateTimeOffset? Updated { get; init; }
}

/// <summary>
/// Request body for <c>POST /components</c>. Flat, not JSON:API-enveloped —
/// <see cref="MachineId"/>/<see cref="Fingerprint"/>/<see cref="Name"/> are required.
/// </summary>
public sealed record CreateComponentRequest
{
    /// <summary>The ID of the machine to attach this component to.</summary>
    [JsonPropertyName("machine_id")]
    public required Guid MachineId { get; init; }

    /// <summary>The component's fingerprint identifier.</summary>
    [JsonPropertyName("fingerprint")]
    public required string Fingerprint { get; init; }

    /// <summary>The component's display name.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Arbitrary caller-supplied metadata to attach to the component.</summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}

/// <summary>A single page of a keyset-paginated (<c>limit</c>/<c>page[after]</c>) listing.</summary>
public sealed record Page<T>
{
    /// <summary>The items in this page of results.</summary>
    public required IReadOnlyList<T> Items { get; init; }

    /// <summary>The cursor to pass as <c>page[after]</c> to fetch the next page, or <see langword="null"/> if this was the last page.</summary>
    public string? NextCursor { get; init; }
}
