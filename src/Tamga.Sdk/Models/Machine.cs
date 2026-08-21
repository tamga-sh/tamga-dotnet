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
    /// Wire value <c>DEAD</c> — the window elapsed with no ping. That is ALL it means, and no call
    /// this SDK currently makes can return it.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Unreachable from every write route.</b> <c>ping-heartbeat</c> writes
    /// <c>last_heartbeat_at = NOW()</c> and derives the status from that same timestamp
    /// (<c>ALIVE</c>/<c>RESURRECTED</c>); <c>create</c> never sets the column and
    /// <c>reset-heartbeat</c> nulls it (<c>NOT_STARTED</c>); and license validation never emits
    /// <c>HEARTBEAT_DEAD</c>. The single place this value reaches a caller today is the machine
    /// inside a checked-out <c>.machine</c> file
    /// (<see cref="Checkout.MachineFile.VerifyAndDecrypt(Tamga.Sdk.Models.LicenseScheme, System.ReadOnlySpan{byte}, string, string)"/>), which is resolved through a read
    /// query. Treat <c>if (status == Dead)</c> written against a ping result as dead code.
    ///
    /// ⚠ <b>And where it IS observable, it does not mean the row was culled</b>, deleted, or that
    /// its seat was released. The server computes this field from <c>last_heartbeat_at</c> alone
    /// and never looks at <c>policy.require_heartbeat</c>; the cull job that deletes rows is gated
    /// on <c>require_heartbeat</c>, which defaults to <c>FALSE</c>, so under a default policy
    /// nothing is ever culled and a machine can sit at <c>DEAD</c> indefinitely with its row
    /// intact. A ping revives it. Only a <c>404</c> from the ping proves the row is gone. See
    /// <see cref="HeartbeatScheduler"/>.
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

    /// <summary>When the machine row was created.</summary>
    [JsonPropertyName("created")]
    public DateTimeOffset? Created { get; init; }

    /// <summary>When the machine row was last updated.</summary>
    [JsonPropertyName("updated")]
    public DateTimeOffset? Updated { get; init; }
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
    /// <see cref="TamgaClient.CreateMachineAsync"/>, <see cref="TamgaClient.PingHeartbeatAsync"/>,
    /// <see cref="TamgaClient.ResetHeartbeatAsync"/> and
    /// <see cref="TamgaClient.UpdateMachineAsync"/> return rows from <c>INSERT</c>/
    /// <c>UPDATE … RETURNING</c> statements that do not join <c>policies</c>, so this is computed
    /// against the 600s fallback even when the policy sets a shorter <c>heartbeat_duration</c>.
    /// </description></item>
    /// <item><description>
    /// <see cref="TamgaClient.GetMachineAsync"/> and <see cref="TamgaClient.ListMachinesAsync"/>
    /// resolve through queries that DO join <c>policies</c>, so both carry the real
    /// policy-derived value — and <c>NextHeartbeatAt - LastHeartbeatAt</c> on either recovers the
    /// effective window directly. <see cref="TamgaClient.GetLicensePolicyAsync"/> is the more
    /// direct route to the same number.
    /// </description></item>
    /// <item><description>
    /// <see cref="Checkout.MachineFile.VerifyAndDecrypt(Tamga.Sdk.Models.LicenseScheme, System.ReadOnlySpan{byte}, string, string)"/> — the machine embedded in a
    /// <c>.machine</c> file from <see cref="TamgaClient.CheckOutMachineAsync"/> — is resolved
    /// through a query that DOES join <c>policies</c>, so there this carries the real
    /// policy-derived value. <c>NextHeartbeatAt - LastHeartbeatAt</c> on such a machine therefore
    /// RECOVERS the effective window — which is how a caller sizes
    /// <see cref="HeartbeatScheduler"/>'s interval without having to be told the policy out of
    /// band. Two caveats: the server computes <c>next_heartbeat_at</c> as
    /// <c>last_heartbeat_at + window</c>, so it is <see langword="null"/>, and the window
    /// unrecoverable, until the machine has pinged at least once; and the value is a snapshot from
    /// the moment the file was issued, so a policy changed afterwards is not reflected in a file
    /// you already hold.
    /// </description></item>
    /// </list>
    /// So "this SDK cannot see the heartbeat window" is false — it can, on three machine routes
    /// and directly via <see cref="TamgaClient.GetLicensePolicyAsync"/>. What it still cannot do
    /// is see the window from a ping, or notice it changing after the fact.
    /// </remarks>
    public DateTimeOffset? NextHeartbeatAt { get; init; }

    /// <summary>When the machine was last checked out (offline <c>.machine</c> file issued).</summary>
    public DateTimeOffset? LastCheckOutAt { get; init; }

    /// <summary>Arbitrary caller-supplied metadata attached to the machine.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; init; }

    /// <summary>When the machine row was created.</summary>
    public DateTimeOffset? Created { get; init; }

    /// <summary>When the machine row was last updated.</summary>
    public DateTimeOffset? Updated { get; init; }

    /// <summary>Flattens a raw JSON:API machine resource into a <see cref="Machine"/>. Shared by <see cref="TamgaClient"/> and <see cref="Checkout.MachineFile"/>.</summary>
    /// <remarks>
    /// Deliberately does not read <c>data.relationships</c>: the server never emits one on a
    /// machine response. A <c>LicenseId</c> property used to be populated from it; it was always
    /// <see langword="null"/>, was marked <c>[Obsolete]</c> in 2.0.0 and removed in 2.1.0 — same
    /// defect, same history and same handling as <see cref="License"/>'s four relationship ids.
    /// Track the license id you activated with on your own side, or pass it back through
    /// <see cref="CreateMachineRequest.LicenseId"/>, which is a live REQUEST field and unrelated.
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
            Created = attrs.Created,
            Updated = attrs.Updated,
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

/// <summary>
/// Request attributes for <c>PATCH /machines/{id}</c>. Every field is optional; omitting one
/// leaves the stored value untouched.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>A field cannot be cleared through this endpoint.</b> The server writes every column with
/// <c>COALESCE($n, col)</c>, so a <see langword="null"/> means "leave alone" and there is no value
/// that means "set back to null". This SDK omits nulls from the request body anyway, which makes
/// the two indistinguishable on the wire — matching the only behaviour the server implements.
/// </para>
/// <para>
/// <see cref="CreateMachineRequest.Fingerprint"/> is deliberately absent: the update handler does
/// not accept it, and neither do the license, policy, owner or group associations or any heartbeat
/// field. Re-fingerprinting a machine means creating a new one.
/// </para>
/// <para>
/// <see cref="Memory"/> and <see cref="Disk"/> are MEGABYTES here too — the same units, and the
/// same 1,048,576× inflation of the license's running total, as on
/// <see cref="CreateMachineRequest"/>.
/// </para>
/// </remarks>
public sealed record UpdateMachineRequest
{
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

    /// <summary>The machine's memory size, in MEGABYTES — see the type-level remarks.</summary>
    public long? Memory { get; init; }

    /// <summary>The machine's disk size, in MEGABYTES — see the type-level remarks.</summary>
    public long? Disk { get; init; }

    /// <summary>Arbitrary caller-supplied metadata to attach to the machine. Replaces the stored object wholesale; it is not merged.</summary>
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}

/// <summary>
/// The outcome of <see cref="TamgaClient.ActivateMachineIdempotentAsync"/>: the machine that is
/// now activated, the license verdict, and whether the machine already existed.
/// </summary>
public sealed record MachineActivation
{
    /// <summary>The activated machine — either the one just created, or the one that already held the fingerprint.</summary>
    public required Machine Machine { get; init; }

    /// <summary>The license validation that ran after activation. Read <see cref="ValidationResult.Code"/> before treating the activation as usable.</summary>
    public required ValidationResult Validation { get; init; }

    /// <summary>
    /// <see langword="true"/> when the server answered <c>409 FINGERPRINT_TAKEN</c> and
    /// <see cref="Machine"/> is a pre-existing row, on this license, that the call did not create.
    /// </summary>
    /// <remarks>
    /// The "on this license" is load-bearing: the lookup behind it is scoped to the license being
    /// activated, so a conflict caused by a machine on a <em>different</em> license re-throws
    /// instead of setting this. Read it as "this license already has this machine".
    ///
    /// It is also the flag that suppresses the over-limit rollback: a machine this call did not
    /// create is never deleted, however the validation comes back. See
    /// <see cref="TamgaClient.ActivateMachineIdempotentAsync"/>.
    /// </remarks>
    public bool AlreadyActivated { get; init; }
}
