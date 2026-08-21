using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamga.Sdk.Models;

/// <summary>
/// A machine's heartbeat state, evaluated against the governing policy's heartbeat window.
/// </summary>
/// <remarks>
/// The window is <c>policy.heartbeat_duration</c> when that column is set, and 600s (10 min) only
/// as the fallback when it is null (<c>Policy::effective_heartbeat_duration_secs</c>). Earlier
/// versions of this comment claimed the 600s window was hardcoded and that
/// <c>heartbeat_duration</c> was ignored — that was wrong, and a scheduler built on it will ping
/// too slowly for any policy that sets a shorter duration. See
/// <see cref="HeartbeatScheduler.ServerHeartbeatWindowSeconds"/> for what this SDK can and cannot
/// see of the window in force.
/// </remarks>
[JsonConverter(typeof(HeartbeatStatusConverter))]
public enum HeartbeatStatus
{
    /// <summary>Wire value <c>NOT_STARTED</c> — never pinged.</summary>
    NotStarted,

    /// <summary>Wire value <c>ALIVE</c> — pinged within the window.</summary>
    Alive,

    /// <summary>
    /// Wire value <c>DEAD</c> — the window elapsed with no ping. That is ALL it means.
    /// </summary>
    /// <remarks>
    /// ⚠ It does NOT mean the machine row was culled, deleted, or that its seat was released. The
    /// server computes this field from <c>last_heartbeat_at</c> alone and never looks at
    /// <c>policy.require_heartbeat</c>; the cull job that deletes rows is gated on
    /// <c>require_heartbeat</c>, which defaults to <c>FALSE</c>, so under a default policy nothing
    /// is ever culled and a machine can report <c>DEAD</c> forever with its row intact. Keep
    /// pinging — the ping succeeds and revives it. Only a <c>404</c> from the ping proves the row
    /// is gone. See <see cref="HeartbeatScheduler"/>.
    /// </remarks>
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

    /// <summary>The machine's memory size, in MEGABYTES — see <see cref="CreateMachineRequest.Memory"/>.</summary>
    [JsonPropertyName("memory")]
    public long? Memory { get; init; }

    /// <summary>The machine's disk size, in MEGABYTES — see <see cref="CreateMachineRequest.Disk"/>.</summary>
    [JsonPropertyName("disk")]
    public long? Disk { get; init; }

    /// <summary>The machine's current heartbeat status.</summary>
    [JsonPropertyName("heartbeat_status")]
    [JsonConverter(typeof(HeartbeatStatusConverter))]
    public HeartbeatStatus HeartbeatStatus { get; init; }

    /// <summary>When the machine last sent a heartbeat ping.</summary>
    [JsonPropertyName("last_heartbeat_at")]
    public DateTimeOffset? LastHeartbeatAt { get; init; }

    /// <summary>When the machine's next heartbeat ping is due. Route-dependent — see <see cref="Machine.NextHeartbeatAt"/>.</summary>
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

    /// <summary>The machine's memory size, in MEGABYTES — see <see cref="CreateMachineRequest.Memory"/>.</summary>
    public long? Memory { get; init; }

    /// <summary>The machine's disk size, in MEGABYTES — see <see cref="CreateMachineRequest.Disk"/>.</summary>
    public long? Disk { get; init; }

    /// <summary>The machine's current heartbeat status.</summary>
    public HeartbeatStatus HeartbeatStatus { get; init; }

    /// <summary>When the machine last sent a heartbeat ping.</summary>
    public DateTimeOffset? LastHeartbeatAt { get; init; }

    /// <summary>
    /// When the machine's next heartbeat ping is due. Whether this reflects the policy's real
    /// window or the 600s fallback depends on WHICH CALL returned the machine — see the remarks.
    /// </summary>
    /// <remarks>
    /// The server derives this (and <see cref="HeartbeatStatus"/>) from a window carried on the
    /// row, which is populated only when the query that loaded the machine joined <c>policies</c>.
    /// It is therefore route-dependent, and the split lands squarely across this SDK's surface:
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="TamgaClient.CreateMachineAsync"/>, <see cref="TamgaClient.PingHeartbeatAsync"/>
    /// and <see cref="TamgaClient.ResetHeartbeatAsync"/> return rows from <c>INSERT</c>/
    /// <c>UPDATE … RETURNING</c> statements that do not join <c>policies</c>, so this is computed
    /// against the 600s fallback even when the policy sets a shorter <c>heartbeat_duration</c>.
    /// </description></item>
    /// <item><description>
    /// <see cref="Checkout.MachineFile.VerifyAndDecrypt"/> — the machine embedded in a
    /// <c>.machine</c> file from <see cref="TamgaClient.CheckOutMachineAsync"/> — is resolved
    /// through a query that DOES join <c>policies</c>, so there this carries the real
    /// policy-derived value. Reading <c>NextHeartbeatAt - LastHeartbeatAt</c> off a checked-out
    /// machine is the one way this SDK can observe the effective window.
    /// </description></item>
    /// </list>
    /// So "this SDK cannot see the heartbeat window" would be false as a blanket claim — it can,
    /// on exactly one route. What it cannot do is see it from a ping.
    /// </remarks>
    public DateTimeOffset? NextHeartbeatAt { get; init; }

    /// <summary>When the machine was last checked out (offline <c>.machine</c> file issued).</summary>
    public DateTimeOffset? LastCheckOutAt { get; init; }

    /// <summary>Always <see langword="null"/>. See the obsolete note.</summary>
    [Obsolete("Always null: the server's machine serializer emits only { type, id, attributes } — `relationships` appears on the machine CREATE request body but never on any response, so there is nothing for this to be read from. Track the license id you activated with on your own side. Scheduled for removal in the next minor release.")]
    public Guid? LicenseId { get; init; }

    /// <summary>Arbitrary caller-supplied metadata attached to the machine.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; init; }

    /// <summary>Flattens a raw JSON:API machine resource into a <see cref="Machine"/>. Shared by <see cref="TamgaClient"/> and <see cref="Checkout.MachineFile"/>.</summary>
    /// <remarks>
    /// Deliberately does not read <c>data.relationships</c>: the server never emits one on a
    /// machine response. <c>LicenseId</c>, which used to be populated from it, is obsolete and left
    /// unset — same defect and same handling as <see cref="License"/>'s four relationship ids.
    /// </remarks>
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

    /// <summary>The machine's memory size, in MEGABYTES.</summary>
    /// <remarks>
    /// MEGABYTES, not bytes. The server stores this column in megabytes and rolls it straight into
    /// the license's running <c>machines_memory_count</c>, which is what the
    /// <c>MEMORY_LIMIT_EXCEEDED</c> check measures. Reporting 16 GB as <c>17179869184</c> instead
    /// of <c>16384</c> inflates that total by a factor of 1,048,576 and locks the license out of
    /// its next activation — see <see cref="MemoryLimitExceededException"/>.
    /// </remarks>
    public long? Memory { get; init; }

    /// <summary>The machine's disk size, in MEGABYTES.</summary>
    /// <remarks>MEGABYTES, not bytes — same units and same failure mode as <see cref="Memory"/>.</remarks>
    public long? Disk { get; init; }

    /// <summary>Arbitrary caller-supplied metadata to attach to the machine.</summary>
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}
