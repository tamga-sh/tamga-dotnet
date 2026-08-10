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
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("machine_id")]
    public Guid MachineId { get; init; }

    /// <summary>
    /// ⚠ Modeled as <see cref="string"/>, NOT an integer — the API types PIDs as strings on the
    /// wire. A client that coerces this to <c>int</c>/<c>long</c> will reject or silently mangle
    /// legitimate values and diverge from the wire contract.
    /// </summary>
    [JsonPropertyName("pid")]
    public string Pid { get; init; } = "";

    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }

    [JsonPropertyName("created")]
    public DateTimeOffset? Created { get; init; }

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
    [JsonPropertyName("machine_id")]
    public required Guid MachineId { get; init; }

    [JsonPropertyName("pid")]
    public required string Pid { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}
