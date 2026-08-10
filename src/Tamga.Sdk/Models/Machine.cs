using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamga.Sdk.Models;

/// <summary>
/// A machine's heartbeat state. GOTCHA: the 600s (10 min) heartbeat window is hardcoded
/// server-side, NOT driven by <c>policy.heartbeat_duration</c>.
/// </summary>
[JsonConverter(typeof(HeartbeatStatusConverter))]
public enum HeartbeatStatus
{
    /// <summary>Wire value <c>NOT_STARTED</c> — never pinged.</summary>
    NotStarted,

    /// <summary>Wire value <c>ALIVE</c> — pinged within the window.</summary>
    Alive,

    /// <summary>Wire value <c>DEAD</c> — window elapsed with no ping.</summary>
    Dead,

    /// <summary>Wire value <c>RESURRECTED</c> — a new ping arrived after a death event was already recorded, within the resurrection grace window.</summary>
    Resurrected,
}

/// <summary>Converts <see cref="HeartbeatStatus"/> to/from its wire string.</summary>
public sealed class HeartbeatStatusConverter : JsonConverter<HeartbeatStatus>
{
    public override HeartbeatStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.GetString() switch
        {
            "ALIVE" => HeartbeatStatus.Alive,
            "DEAD" => HeartbeatStatus.Dead,
            "RESURRECTED" => HeartbeatStatus.Resurrected,
            _ => HeartbeatStatus.NotStarted,
        };
    }

    public override void Write(Utf8JsonWriter writer, HeartbeatStatus value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            HeartbeatStatus.Alive => "ALIVE",
            HeartbeatStatus.Dead => "DEAD",
            HeartbeatStatus.Resurrected => "RESURRECTED",
            _ => "NOT_STARTED",
        });
    }
}

/// <summary>The JSON:API <c>attributes</c> bag for a machine resource.</summary>
public sealed record MachineAttributes
{
    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("ip")]
    public string? Ip { get; init; }

    [JsonPropertyName("hostname")]
    public string? Hostname { get; init; }

    [JsonPropertyName("platform")]
    public string? Platform { get; init; }

    [JsonPropertyName("cores")]
    public int? Cores { get; init; }

    [JsonPropertyName("memory")]
    public long? Memory { get; init; }

    [JsonPropertyName("disk")]
    public long? Disk { get; init; }

    [JsonPropertyName("heartbeat_status")]
    [JsonConverter(typeof(HeartbeatStatusConverter))]
    public HeartbeatStatus HeartbeatStatus { get; init; }

    [JsonPropertyName("last_heartbeat_at")]
    public DateTimeOffset? LastHeartbeatAt { get; init; }

    [JsonPropertyName("next_heartbeat_at")]
    public DateTimeOffset? NextHeartbeatAt { get; init; }

    [JsonPropertyName("last_check_out_at")]
    public DateTimeOffset? LastCheckOutAt { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}

/// <summary>
/// A machine resource, flattened from the JSON:API <c>data.attributes</c> + <c>data.id</c> +
/// <c>data.relationships</c> shape, mirroring <see cref="License"/>'s flattening pattern.
/// </summary>
public sealed record Machine
{
    public Guid Id { get; init; }
    public string? Fingerprint { get; init; }
    public string? Name { get; init; }
    public string? Ip { get; init; }
    public string? Hostname { get; init; }
    public string? Platform { get; init; }
    public int? Cores { get; init; }
    public long? Memory { get; init; }
    public long? Disk { get; init; }
    public HeartbeatStatus HeartbeatStatus { get; init; }
    public DateTimeOffset? LastHeartbeatAt { get; init; }
    public DateTimeOffset? NextHeartbeatAt { get; init; }
    public DateTimeOffset? LastCheckOutAt { get; init; }
    public Guid? LicenseId { get; init; }
    public Dictionary<string, JsonElement>? Metadata { get; init; }

    /// <summary>Flattens a raw JSON:API machine resource into a <see cref="Machine"/>. Shared by <see cref="TamgaClient"/> and <see cref="Checkout.MachineFile"/>.</summary>
    public static Machine FromResource(JsonApiResource<MachineAttributes> resource)
    {
        var attrs = resource.Attributes ?? new MachineAttributes();
        return new Machine
        {
            Id = resource.Id,
            Fingerprint = attrs.Fingerprint,
            Name = attrs.Name,
            Ip = attrs.Ip,
            Hostname = attrs.Hostname,
            Platform = attrs.Platform,
            Cores = attrs.Cores,
            Memory = attrs.Memory,
            Disk = attrs.Disk,
            HeartbeatStatus = attrs.HeartbeatStatus,
            LastHeartbeatAt = attrs.LastHeartbeatAt,
            NextHeartbeatAt = attrs.NextHeartbeatAt,
            LastCheckOutAt = attrs.LastCheckOutAt,
            LicenseId = resource.Relationships is { } rels && rels.TryGetValue("license", out var rel) ? rel.Data?.Id : null,
            Metadata = attrs.Metadata,
        };
    }
}

/// <summary>Request attributes for <c>POST /machines</c>. <c>Fingerprint</c> and <c>LicenseId</c> are required; everything else is optional.</summary>
public sealed record CreateMachineRequest
{
    public required string Fingerprint { get; init; }
    public required Guid LicenseId { get; init; }
    public string? Name { get; init; }
    public string? Ip { get; init; }
    public string? Hostname { get; init; }
    public string? Platform { get; init; }
    public int? Cores { get; init; }
    public long? Memory { get; init; }
    public long? Disk { get; init; }
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}
