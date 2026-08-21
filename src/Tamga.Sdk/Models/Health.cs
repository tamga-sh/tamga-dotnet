using System.Text.Json.Serialization;

namespace Tamga.Sdk.Models;

/// <summary>
/// The server's liveness answer from <c>GET /v1/health</c>: <c>{ status, version, uptime_secs }</c>.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>This is not a JSON:API document.</b> The handler returns a plain object with
/// <c>Content-Type: application/json</c> — no <c>data</c> envelope, no <c>type</c>, no
/// <c>attributes</c>. Running it through this SDK's envelope decoder yields nothing.
/// </para>
/// <para>
/// It is also a liveness probe, not a readiness one: the handler never touches the database, so a
/// successful response says the process is up and answering, not that it can serve licensing
/// traffic.
/// </para>
/// </remarks>
public sealed record TamgaHealth
{
    /// <summary>Always the literal <c>"ok"</c> — the handler has no other branch, so this carries no information beyond "the response arrived".</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    /// <summary>The server build's version string, compiled in at build time.</summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = "";

    /// <summary>How long the server process has been running, in seconds.</summary>
    /// <remarks>
    /// Note the wire name is <c>uptime_secs</c> — snake_case, unlike the release resource's
    /// camelCase attributes.
    /// </remarks>
    [JsonPropertyName("uptime_secs")]
    public long UptimeSeconds { get; init; }
}
