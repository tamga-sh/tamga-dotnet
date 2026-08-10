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
    /// <summary>Deserializes the wire string into a <see cref="HeartbeatStatus"/>.</summary>
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

    /// <summary>Serializes the <see cref="HeartbeatStatus"/> as its wire string.</summary>
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
    /// <summary>The machine's fingerprint identifier.</summary>
    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; init; }

    /// <summary>The machine's display name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>The machine's IP address.</summary>
    [JsonPropertyName("ip")]
    public string? Ip { get; init; }

    /// <summary>The machine's hostname.</summary>
    [JsonPropertyName("hostname")]
    public string? Hostname { get; init; }

    /// <summary>The machine's platform/OS identifier.</summary>
    [JsonPropertyName("platform")]
    public string? Platform { get; init; }

    /// <summary>The number of CPU cores reported by the machine.</summary>
    [JsonPropertyName("cores")]
    public int? Cores { get; init; }

    /// <summary>The machine's memory size, in bytes.</summary>
    [JsonPropertyName("memory")]
    public long? Memory { get; init; }

    /// <summary>The machine's disk size, in bytes.</summary>
    [JsonPropertyName("disk")]
    public long? Disk { get; init; }

    /// <summary>The machine's current heartbeat status.</summary>
    [JsonPropertyName("heartbeat_status")]
    [JsonConverter(typeof(HeartbeatStatusConverter))]
    public HeartbeatStatus HeartbeatStatus { get; init; }

    /// <summary>When the machine last sent a heartbeat ping.</summary>
    [JsonPropertyName("last_heartbeat_at")]
    public DateTimeOffset? LastHeartbeatAt { get; init; }

    /// <summary>When the machine's next heartbeat ping is due.</summary>
    [JsonPropertyName("next_heartbeat_at")]
    public DateTimeOffset? NextHeartbeatAt { get; init; }

    /// <summary>When the machine was last checked out (offline <c>.machine</c> file issued).</summary>
    [JsonPropertyName("last_check_out_at")]
    public DateTimeOffset? LastCheckOutAt { get; init; }

    /// <summary>Arbitrary caller-supplied metadata attached to the machine.</summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}

/// <summary>
/// A machine resource, flattened from the JSON:API <c>data.attributes</c> + <c>data.id</c> +
/// <c>data.relationships</c> shape, mirroring <see cref="License"/>'s flattening pattern.
/// </summary>
public sealed record Machine
{
    /// <summary>The machine's unique ID.</summary>
    public Guid Id { get; init; }

    /// <summary>The machine's fingerprint identifier.</summary>
    public string? Fingerprint { get; init; }

    /// <summary>The machine's display name.</summary>
    public string? Name { get; init; }

    /// <summary>The machine's IP address.</summary>
    public string? Ip { get; init; }

    /// <summary>The machine's hostname.</summary>
    public string? Hostname { get; init; }

    /// <summary>The machine's platform/OS identifier.</summary>
    public string? Platform { get; init; }

    /// <summary>The number of CPU cores reported by the machine.</summary>
    public int? Cores { get; init; }

    /// <summary>The machine's memory size, in bytes.</summary>
    public long? Memory { get; init; }

    /// <summary>The machine's disk size, in bytes.</summary>
    public long? Disk { get; init; }

    /// <summary>The machine's current heartbeat status.</summary>
    public HeartbeatStatus HeartbeatStatus { get; init; }

    /// <summary>When the machine last sent a heartbeat ping.</summary>
    public DateTimeOffset? LastHeartbeatAt { get; init; }

    /// <summary>When the machine's next heartbeat ping is due.</summary>
    public DateTimeOffset? NextHeartbeatAt { get; init; }

    /// <summary>When the machine was last checked out (offline <c>.machine</c> file issued).</summary>
    public DateTimeOffset? LastCheckOutAt { get; init; }

    /// <summary>The ID of the license this machine is activated against.</summary>
    public Guid? LicenseId { get; init; }

    /// <summary>Arbitrary caller-supplied metadata attached to the machine.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; init; }

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
    /// <summary>The machine's unique fingerprint identifier.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>The ID of the license to activate this machine against.</summary>
    public required Guid LicenseId { get; init; }

    /// <summary>The machine's display name.</summary>
    public string? Name { get; init; }

    /// <summary>The machine's IP address.</summary>
    public string? Ip { get; init; }

    /// <summary>The machine's hostname.</summary>
    public string? Hostname { get; init; }

    /// <summary>The machine's platform/OS identifier.</summary>
    public string? Platform { get; init; }

    /// <summary>The number of CPU cores to report for the machine.</summary>
    public int? Cores { get; init; }

    /// <summary>The machine's memory size, in bytes.</summary>
    public long? Memory { get; init; }

    /// <summary>The machine's disk size, in bytes.</summary>
    public long? Disk { get; init; }

    /// <summary>Arbitrary caller-supplied metadata to attach to the machine.</summary>
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}
