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

    /// <summary>Arbitrary caller-supplied metadata attached to the process.</summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }

    /// <summary>When the process was created.</summary>
    [JsonPropertyName("created")]
    public DateTimeOffset? Created { get; init; }

    /// <summary>When the process was last updated.</summary>
    [JsonPropertyName("updated")]
    public DateTimeOffset? Updated { get; init; }
}

/// <summary>
/// Request body for <c>POST /processes</c>. Flat, not JSON:API-enveloped.
/// <see cref="Pid"/> is deliberately <see cref="string"/> — see <see cref="Process.Pid"/>'s remarks.
/// It accepts an <see cref="int"/> at the call site (see <c>TamgaClient.CreateProcessAsync</c>)
/// purely for caller convenience, but always serializes as a JSON string.
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
