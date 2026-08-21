using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamga.Sdk.Models;

/// <summary>
/// A machine process resource. <c>POST /processes</c> uses a flat, non-JSON:API-enveloped request
/// body: <c>{ machine_id, pid, metadata }</c>.
/// </summary>
/// <remarks>
/// GOTCHA: processes start <c>ALIVE</c> immediately (heartbeat timestamp set at creation) —
/// unlike machines, which start <c>NOT_STARTED</c>. Process heartbeat window is a hardcoded 30
/// seconds with NO resurrection grace period — a dead process row is deleted immediately, no
/// <c>KeepDead</c> equivalent.
/// </remarks>
public sealed record Process
{
    /// <summary>The process's unique ID.</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    /// <summary>The ID of the machine this process belongs to.</summary>
    [JsonPropertyName("machine_id")]
    public Guid MachineId { get; init; }

    /// <summary>
    /// ⚠ Modeled as <see cref="string"/>, NOT an integer — the API types PIDs as strings on the
    /// wire. A client that coerces this to <c>int</c>/<c>long</c> will reject or silently mangle
    /// legitimate values and diverge from the wire contract.
    /// </summary>
    [JsonPropertyName("pid")]
    public string Pid { get; init; } = "";

    /// <summary>
    /// When the server last recorded a heartbeat for this process. Non-null on every response —
    /// the column is <c>NOT NULL</c> and a process is created with it already set.
    /// </summary>
    /// <remarks>
    /// On a <see cref="TamgaClient.PingProcessAsync"/> response this is the timestamp that call
    /// just wrote, so it is the one durable confirmation that the server recorded the ping. There
    /// is deliberately no heartbeat-STATUS field to go with it: unlike a machine, a process that
    /// misses its window is deleted outright rather than tracked through <c>DEAD</c> /
    /// <c>RESURRECTED</c>, so the only states are "row exists" and "404".
    /// </remarks>
    [JsonPropertyName("last_heartbeat_at")]
    public DateTimeOffset? LastHeartbeatAt { get; init; }

    /// <summary>Arbitrary caller-supplied metadata attached to the process.</summary>
    [JsonPropertyName("metadata")]
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; init; }

    /// <summary>When the process was created.</summary>
    [JsonPropertyName("created")]
    public DateTimeOffset? Created { get; init; }

    /// <summary>When the process was last updated.</summary>
    [JsonPropertyName("updated")]
    public DateTimeOffset? Updated { get; init; }

    /// <summary>
    /// Flattens a raw JSON:API process resource, taking <see cref="Id"/> from <c>data.id</c> and
    /// everything else from <c>data.attributes</c>.
    /// </summary>
    /// <remarks>
    /// This type doubles as its own attributes bag — see <see cref="Component.FromResource"/> for
    /// why.
    /// </remarks>
    /// <param name="resource">The JSON:API resource object to flatten.</param>
    public static Process FromResource(JsonApiResource<Process> resource) =>
        (resource.Attributes ?? new Process()) with { Id = resource.Id };
}

/// <summary>
/// Request body for <c>POST /processes</c>. Flat, not JSON:API-enveloped.
/// <see cref="Pid"/> is deliberately <see cref="string"/> — see <see cref="Process.Pid"/>'s remarks.
/// <c>TamgaClient.CreateProcessAsync</c> takes a <see cref="string"/> too, rather than accepting an
/// <see cref="int"/> for convenience: an implicit numeric-to-string conversion at the call site is
/// exactly the coercion this type exists to prevent.
/// </summary>
public sealed record CreateProcessRequest
{
    /// <summary>The ID of the machine this process belongs to.</summary>
    [JsonPropertyName("machine_id")]
    public required Guid MachineId { get; init; }

    /// <summary>The process ID to report, as a string — see <see cref="Process.Pid"/>'s remarks.</summary>
    [JsonPropertyName("pid")]
    public required string Pid { get; init; }

    /// <summary>Arbitrary caller-supplied metadata to attach to the process.</summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}
