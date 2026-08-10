using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamga.Sdk.Models;

/// <summary>
/// A machine component resource. <c>POST /components</c> uses a flat, non-JSON:API-enveloped
/// request body (asymmetric vs. <c>POST /machines</c>): <c>{ machine_id, fingerprint, name, metadata }</c>.
/// </summary>
public sealed record Component
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("machine_id")]
    public Guid MachineId { get; init; }

    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }

    [JsonPropertyName("created")]
    public DateTimeOffset? Created { get; init; }

    [JsonPropertyName("updated")]
    public DateTimeOffset? Updated { get; init; }
}

/// <summary>
/// Request body for <c>POST /components</c>. Flat, not JSON:API-enveloped —
/// <see cref="MachineId"/>/<see cref="Fingerprint"/>/<see cref="Name"/> are required.
/// </summary>
public sealed record CreateComponentRequest
{
    [JsonPropertyName("machine_id")]
    public required Guid MachineId { get; init; }

    [JsonPropertyName("fingerprint")]
    public required string Fingerprint { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}

/// <summary>A single page of a keyset-paginated (<c>limit</c>/<c>page[after]</c>) listing.</summary>
public sealed record Page<T>
{
    public required IReadOnlyList<T> Items { get; init; }

    /// <summary>The cursor to pass as <c>page[after]</c> to fetch the next page, or <see langword="null"/> if this was the last page.</summary>
    public string? NextCursor { get; init; }
}
