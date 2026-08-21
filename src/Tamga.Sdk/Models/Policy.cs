using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamga.Sdk.Models;

/// <summary>
/// The key/checkout signing algorithm configured on a license's policy. <c>None</c> means the
/// policy has no scheme set — a legacy plain key string, unsigned.
/// </summary>
[JsonConverter(typeof(LicenseSchemeConverter))]
public enum LicenseScheme
{
    /// <summary>No scheme configured — legacy plain key string, unsigned.</summary>
    None,

    /// <summary>Wire value <c>ED25519_SIGN</c>. Also the sole scheme used for license checkout (§E), independent of this field.</summary>
    Ed25519Sign,

    /// <summary>Wire value <c>RSA_2048_PKCS1_SIGN</c>.</summary>
    Rsa2048Pkcs1Sign,

    /// <summary>Wire value <c>RSA_2048_PKCS1_PSS_SIGN</c>.</summary>
    Rsa2048Pkcs1PssSign,

    /// <summary>Wire value <c>ECDSA_P256_SIGN</c>.</summary>
    EcdsaP256Sign,

    /// <summary>
    /// Wire value <c>RSA_2048_JWT_RS256</c>. Explicitly rejected server-side for machine files
    /// (<c>422 SCHEME_NOT_SUPPORTED</c>) — <c>MachineFile</c> must throw
    /// <see cref="SchemeNotSupportedException"/> rather than attempt JWT/RS256 verification.
    /// </summary>
    Rsa2048JwtRs256,
}

/// <summary>
/// Converts <see cref="LicenseScheme"/> to/from its wire string. An empty string or missing value
/// maps to <see cref="LicenseScheme.None"/> (legacy unsigned key).
/// </summary>
public sealed class LicenseSchemeConverter : JsonConverter<LicenseScheme>
{
    /// <summary>Deserializes the wire string into a <see cref="LicenseScheme"/>.</summary>
    public override LicenseScheme Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value switch
        {
            null or "" => LicenseScheme.None,
            "ED25519_SIGN" => LicenseScheme.Ed25519Sign,
            "RSA_2048_PKCS1_SIGN" => LicenseScheme.Rsa2048Pkcs1Sign,
            "RSA_2048_PKCS1_PSS_SIGN" => LicenseScheme.Rsa2048Pkcs1PssSign,
            "ECDSA_P256_SIGN" => LicenseScheme.EcdsaP256Sign,
            "RSA_2048_JWT_RS256" => LicenseScheme.Rsa2048JwtRs256,
            _ => LicenseScheme.None,
        };
    }

    /// <summary>Serializes the <see cref="LicenseScheme"/> as its wire string.</summary>
    public override void Write(Utf8JsonWriter writer, LicenseScheme value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            LicenseScheme.Ed25519Sign => "ED25519_SIGN",
            LicenseScheme.Rsa2048Pkcs1Sign => "RSA_2048_PKCS1_SIGN",
            LicenseScheme.Rsa2048Pkcs1PssSign => "RSA_2048_PKCS1_PSS_SIGN",
            LicenseScheme.EcdsaP256Sign => "ECDSA_P256_SIGN",
            LicenseScheme.Rsa2048JwtRs256 => "RSA_2048_JWT_RS256",
            _ => "",
        });
    }
}

/// <summary>
/// How many licenses over the relevant <c>max_*</c> policy limit (machines/cores/memory/disk/
/// processes) are tolerated before validation fails. Never applies to <c>uses</c>, which is a
/// strict <c>&gt;=</c> comparison regardless of strategy.
/// </summary>
[JsonConverter(typeof(OverageStrategyConverter))]
public enum OverageStrategy
{
    /// <summary>Wire value <c>NO_OVERAGE</c>. Limit enforced as-is (x1). Also the fallback for the non-real <c>DENY_ACCESS</c> string a freshly-created policy may report.</summary>
    NoOverage,

    /// <summary>Wire value <c>ALLOW_1_25X_OVERAGE</c>.</summary>
    Allow125xOverage,

    /// <summary>Wire value <c>ALLOW_1_5X_OVERAGE</c>.</summary>
    Allow15xOverage,

    /// <summary>Wire value <c>ALLOW_2X_OVERAGE</c>.</summary>
    Allow2xOverage,

    /// <summary>Wire value <c>ALWAYS_ALLOW_OVERAGE</c>. Limit ignored entirely.</summary>
    AlwaysAllowOverage,
}

/// <summary>
/// Converts <see cref="OverageStrategy"/> to/from its wire string.
/// </summary>
/// <remarks>
/// ⚠ CRITICAL: a freshly-created policy on the server can report the literal string
/// <c>"DENY_ACCESS"</c>, which is NOT a real <see cref="OverageStrategy"/> variant — the server
/// silently treats it as <see cref="OverageStrategy.NoOverage"/> (see gap #9 in the Tamga API
/// protocol specification). This converter must decode that string to
/// <see cref="OverageStrategy.NoOverage"/> without throwing, and must NOT invent a fake
/// <c>DenyAccess</c> member that implies restrictive behavior the server doesn't actually apply.
/// Any other unrecognized string also falls back to <see cref="OverageStrategy.NoOverage"/>
/// rather than throwing, matching the server's own fallback in
/// <c>PolicyAttributes::overage_strategy_parsed</c>.
/// </remarks>
public sealed class OverageStrategyConverter : JsonConverter<OverageStrategy>
{
    /// <summary>Deserializes the wire string into an <see cref="OverageStrategy"/>.</summary>
    public override OverageStrategy Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value switch
        {
            "NO_OVERAGE" => OverageStrategy.NoOverage,
            "ALLOW_1_25X_OVERAGE" => OverageStrategy.Allow125xOverage,
            "ALLOW_1_5X_OVERAGE" => OverageStrategy.Allow15xOverage,
            "ALLOW_2X_OVERAGE" => OverageStrategy.Allow2xOverage,
            "ALWAYS_ALLOW_OVERAGE" => OverageStrategy.AlwaysAllowOverage,
            // "DENY_ACCESS" (non-real default) and any other unrecognized value fall back to
            // NoOverage, matching the server's own fallback — see remarks above.
            _ => OverageStrategy.NoOverage,
        };
    }

    /// <summary>Serializes the <see cref="OverageStrategy"/> as its wire string.</summary>
    public override void Write(Utf8JsonWriter writer, OverageStrategy value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            OverageStrategy.NoOverage => "NO_OVERAGE",
            OverageStrategy.Allow125xOverage => "ALLOW_1_25X_OVERAGE",
            OverageStrategy.Allow15xOverage => "ALLOW_1_5X_OVERAGE",
            OverageStrategy.Allow2xOverage => "ALLOW_2X_OVERAGE",
            OverageStrategy.AlwaysAllowOverage => "ALWAYS_ALLOW_OVERAGE",
            _ => "NO_OVERAGE",
        });
    }
}

/// <summary>What happens to a machine row once it's been <c>DEAD</c> for longer than its resurrection grace window.</summary>
[JsonConverter(typeof(HeartbeatCullStrategyConverter))]
public enum HeartbeatCullStrategy
{
    /// <summary>Wire value <c>DEACTIVATE_DEAD</c> — the machine row is deleted.</summary>
    DeactivateDead,

    /// <summary>Wire value <c>KEEP_DEAD</c> — the machine row is kept, marked dead.</summary>
    KeepDead,
}

/// <summary>Converts <see cref="HeartbeatCullStrategy"/> to/from its wire string.</summary>
public sealed class HeartbeatCullStrategyConverter : JsonConverter<HeartbeatCullStrategy>
{
    /// <summary>Deserializes the wire string into a <see cref="HeartbeatCullStrategy"/>.</summary>
    public override HeartbeatCullStrategy Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.GetString() switch
        {
            "KEEP_DEAD" => HeartbeatCullStrategy.KeepDead,
            _ => HeartbeatCullStrategy.DeactivateDead,
        };
    }

    /// <summary>Serializes the <see cref="HeartbeatCullStrategy"/> as its wire string.</summary>
    public override void Write(Utf8JsonWriter writer, HeartbeatCullStrategy value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value == HeartbeatCullStrategy.KeepDead ? "KEEP_DEAD" : "DEACTIVATE_DEAD");
    }
}

/// <summary>
/// Grace window after a machine is marked <c>DEAD</c> during which a new heartbeat ping revives
/// it (transitions to <c>RESURRECTED</c>) instead of it being culled per
/// <see cref="HeartbeatCullStrategy"/>.
/// </summary>
[JsonConverter(typeof(HeartbeatResurrectionStrategyConverter))]
public enum HeartbeatResurrectionStrategy
{
    /// <summary>Wire value <c>NO_REVIVE</c>. Also the fallback for the non-real <c>NO_RESURRECTION</c> string a freshly-created policy may report.</summary>
    NoRevive,

    /// <summary>Wire value <c>1_MINUTE_REVIVE</c>.</summary>
    OneMinuteRevive,

    /// <summary>Wire value <c>2_MINUTE_REVIVE</c>.</summary>
    TwoMinuteRevive,

    /// <summary>Wire value <c>5_MINUTE_REVIVE</c>.</summary>
    FiveMinuteRevive,

    /// <summary>Wire value <c>10_MINUTE_REVIVE</c>.</summary>
    TenMinuteRevive,

    /// <summary>Wire value <c>15_MINUTE_REVIVE</c>.</summary>
    FifteenMinuteRevive,

    /// <summary>Wire value <c>ALWAYS_REVIVE</c>. No grace-window limit — always revives.</summary>
    AlwaysRevive,
}

/// <summary>
/// Converts <see cref="HeartbeatResurrectionStrategy"/> to/from its wire string.
/// </summary>
/// <remarks>
/// ⚠ CRITICAL: a freshly-created policy can report the literal string <c>"NO_RESURRECTION"</c>,
/// which is NOT a real variant of this enum — the server silently treats it as
/// <see cref="HeartbeatResurrectionStrategy.NoRevive"/> (gap #9). This converter decodes that
/// string (and any other unrecognized string) to <see cref="HeartbeatResurrectionStrategy.NoRevive"/>
/// without throwing, and does NOT invent a fake <c>NoResurrection</c> member.
/// </remarks>
public sealed class HeartbeatResurrectionStrategyConverter : JsonConverter<HeartbeatResurrectionStrategy>
{
    /// <summary>Deserializes the wire string into a <see cref="HeartbeatResurrectionStrategy"/>.</summary>
    public override HeartbeatResurrectionStrategy Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.GetString() switch
        {
            "1_MINUTE_REVIVE" => HeartbeatResurrectionStrategy.OneMinuteRevive,
            "2_MINUTE_REVIVE" => HeartbeatResurrectionStrategy.TwoMinuteRevive,
            "5_MINUTE_REVIVE" => HeartbeatResurrectionStrategy.FiveMinuteRevive,
            "10_MINUTE_REVIVE" => HeartbeatResurrectionStrategy.TenMinuteRevive,
            "15_MINUTE_REVIVE" => HeartbeatResurrectionStrategy.FifteenMinuteRevive,
            "ALWAYS_REVIVE" => HeartbeatResurrectionStrategy.AlwaysRevive,
            // "NO_RESURRECTION" (non-real default) and any other unrecognized value fall back to
            // NoRevive, matching the server's own fallback — see remarks above.
            _ => HeartbeatResurrectionStrategy.NoRevive,
        };
    }

    /// <summary>Serializes the <see cref="HeartbeatResurrectionStrategy"/> as its wire string.</summary>
    public override void Write(Utf8JsonWriter writer, HeartbeatResurrectionStrategy value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            HeartbeatResurrectionStrategy.OneMinuteRevive => "1_MINUTE_REVIVE",
            HeartbeatResurrectionStrategy.TwoMinuteRevive => "2_MINUTE_REVIVE",
            HeartbeatResurrectionStrategy.FiveMinuteRevive => "5_MINUTE_REVIVE",
            HeartbeatResurrectionStrategy.TenMinuteRevive => "10_MINUTE_REVIVE",
            HeartbeatResurrectionStrategy.FifteenMinuteRevive => "15_MINUTE_REVIVE",
            HeartbeatResurrectionStrategy.AlwaysRevive => "ALWAYS_REVIVE",
            _ => "NO_REVIVE",
        });
    }
}

/// <summary>
/// How often check-in is required, if <c>require_check_in</c> is set. Wire values are lowercase —
/// inconsistent with the SCREAMING_SNAKE_CASE convention used by every other enum in this SDK.
/// </summary>
[JsonConverter(typeof(CheckInIntervalConverter))]
public enum CheckInInterval
{
    /// <summary>Wire value <c>day</c>.</summary>
    Day,

    /// <summary>Wire value <c>week</c>.</summary>
    Week,

    /// <summary>Wire value <c>month</c>.</summary>
    Month,

    /// <summary>Wire value <c>year</c>.</summary>
    Year,
}

/// <summary>Converts <see cref="CheckInInterval"/> to/from its lowercase wire string.</summary>
public sealed class CheckInIntervalConverter : JsonConverter<CheckInInterval>
{
    /// <summary>Deserializes the lowercase wire string into a <see cref="CheckInInterval"/>.</summary>
    public override CheckInInterval Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.GetString() switch
        {
            "week" => CheckInInterval.Week,
            "month" => CheckInInterval.Month,
            "year" => CheckInInterval.Year,
            _ => CheckInInterval.Day,
        };
    }

    /// <summary>Serializes the <see cref="CheckInInterval"/> as its lowercase wire string.</summary>
    public override void Write(Utf8JsonWriter writer, CheckInInterval value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            CheckInInterval.Week => "week",
            CheckInInterval.Month => "month",
            CheckInInterval.Year => "year",
            _ => "day",
        });
    }
}

/// <summary>
/// Well-known string constants for the free-text policy fields that have no backing server enum
/// (the server branches on literal string match and treats anything else as "deny/default" — see
/// <see cref="Policy.ExpirationStrategy"/>, <see cref="Policy.RenewalBasis"/>,
/// <see cref="Policy.AuthenticationStrategy"/>). These are modeled as plain strings, not closed
/// C# enums, so an unrecognized future value round-trips instead of failing to deserialize.
/// </summary>
public static class PolicyStrategies
{
    /// <summary>Wire value for <see cref="Policy.ExpirationStrategy"/>: access is restricted once the license expires.</summary>
    public const string RestrictAccess = "RESTRICT_ACCESS";

    /// <summary>Wire value for <see cref="Policy.ExpirationStrategy"/>: access is maintained past expiry.</summary>
    public const string MaintainAccess = "MAINTAIN_ACCESS";

    /// <summary>Wire value for <see cref="Policy.ExpirationStrategy"/>: access is always allowed, regardless of expiry.</summary>
    public const string AllowAccess = "ALLOW_ACCESS";

    /// <summary>
    /// Wire value for <see cref="Policy.ExpirationStrategy"/>: the credential itself is revoked at
    /// expiry. The one expiration strategy that changes authentication rather than validation.
    /// </summary>
    /// <remarks>
    /// Under <see cref="RestrictAccess"/>, <see cref="MaintainAccess"/> and
    /// <see cref="AllowAccess"/> an expired license still authenticates and validation answers
    /// <see cref="ValidationCode.Expired"/>. Under this strategy — and under any unrecognized
    /// value, which fails closed the same way — license-key auth is refused outright with
    /// <c>401 LICENSE_EXPIRED</c> (<see cref="LicenseExpiredException"/>), before any endpoint
    /// logic runs.
    /// </remarks>
    public const string RevokeAccess = "REVOKE_ACCESS";

    /// <summary>Wire value for <see cref="Policy.RenewalBasis"/>: renewal extends from the current expiry date.</summary>
    public const string FromExpiry = "FROM_EXPIRY";

    /// <summary>Wire value for <see cref="Policy.RenewalBasis"/>: renewal extends from the current time.</summary>
    public const string FromNow = "FROM_NOW";

    /// <summary>
    /// Wire value for <see cref="Policy.AuthenticationStrategy"/>: token-based authentication.
    /// This is the server's column DEFAULT, and it REFUSES license keys — see
    /// <see cref="Policy.AuthenticationStrategy"/>.
    /// </summary>
    public const string Token = "TOKEN";

    /// <summary>Wire value for <see cref="Policy.AuthenticationStrategy"/>: license-key-based authentication. One of the two values that let this SDK's primary transport work at all.</summary>
    public const string License = "LICENSE";

    /// <summary>Wire value for <see cref="Policy.AuthenticationStrategy"/>: both token and license-key authentication.</summary>
    public const string Mixed = "MIXED";

    /// <summary>
    /// Wire value for <see cref="Policy.AuthenticationStrategy"/>: no authentication strategy
    /// selected. At the license-key auth gate this behaves exactly like <see cref="Token"/> —
    /// the key is refused — it does NOT mean "auth is off".
    /// </summary>
    public const string None = "NONE";
}

/// <summary>
/// A license policy's full attribute set. The server's <c>GET</c> response omits
/// <see cref="MaxMemory"/> and <see cref="MaxDisk"/> even though both are enforced during
/// validation — an SDK cannot introspect these two limits client-side, only observe
/// <c>TooMuchMemory</c>/<c>TooMuchDisk</c> on validation failure. Model them nullable, never
/// required.
/// </summary>
public sealed record Policy
{
    /// <summary>The policy's unique identifier.</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    /// <summary>The maximum number of machines allowed under this policy.</summary>
    [JsonPropertyName("max_machines")]
    public int? MaxMachines { get; init; }

    /// <summary>The maximum number of CPU cores allowed under this policy.</summary>
    [JsonPropertyName("max_cores")]
    public int? MaxCores { get; init; }

    /// <summary>The maximum number of processes allowed under this policy.</summary>
    [JsonPropertyName("max_processes")]
    public int? MaxProcesses { get; init; }

    /// <summary>The maximum number of uses allowed under this policy.</summary>
    [JsonPropertyName("max_uses")]
    public int? MaxUses { get; init; }

    /// <summary>Not present on the server's GET response even though it is enforced — see type-level remarks.</summary>
    [JsonPropertyName("max_memory")]
    public int? MaxMemory { get; init; }

    /// <summary>Not present on the server's GET response even though it is enforced — see type-level remarks.</summary>
    [JsonPropertyName("max_disk")]
    public int? MaxDisk { get; init; }

    /// <summary>Whether periodic check-in is required to keep a license valid.</summary>
    [JsonPropertyName("require_check_in")]
    public bool RequireCheckIn { get; init; }

    /// <summary>The required check-in interval, if <see cref="RequireCheckIn"/> is set.</summary>
    [JsonPropertyName("check_in_interval")]
    [JsonConverter(typeof(CheckInIntervalConverter))]
    public CheckInInterval? CheckInInterval { get; init; }

    /// <summary>Whether machines must send periodic heartbeats to stay alive.</summary>
    [JsonPropertyName("require_heartbeat")]
    public bool RequireHeartbeat { get; init; }

    /// <summary>
    /// GOTCHA: not actually used to compute the server's heartbeat window — that window is
    /// hardcoded to 600s regardless of this value (see gap #8). Do not use this to compute a
    /// client-side heartbeat scheduler interval.
    /// </summary>
    [JsonPropertyName("heartbeat_duration")]
    public int? HeartbeatDuration { get; init; }

    /// <summary>How overage beyond the policy's <c>max_*</c> limits is tolerated.</summary>
    [JsonPropertyName("overage_strategy")]
    [JsonConverter(typeof(OverageStrategyConverter))]
    public OverageStrategy OverageStrategy { get; init; }

    /// <summary>What happens to a machine row once it's been dead for longer than its resurrection grace window.</summary>
    [JsonPropertyName("heartbeat_cull_strategy")]
    [JsonConverter(typeof(HeartbeatCullStrategyConverter))]
    public HeartbeatCullStrategy HeartbeatCullStrategy { get; init; }

    /// <summary>The grace window after a machine is marked dead during which a new heartbeat revives it.</summary>
    [JsonPropertyName("heartbeat_resurrection_strategy")]
    [JsonConverter(typeof(HeartbeatResurrectionStrategyConverter))]
    public HeartbeatResurrectionStrategy HeartbeatResurrectionStrategy { get; init; }

    /// <summary>Free text, no backing enum — see <see cref="PolicyStrategies"/> for the four well-known values. Server default is <see cref="PolicyStrategies.RestrictAccess"/>.</summary>
    /// <remarks>
    /// <see cref="PolicyStrategies.RevokeAccess"/> is the only value that stops an expired license
    /// from authenticating at all; the other three let it in and report
    /// <see cref="ValidationCode.Expired"/> from validation instead.
    /// </remarks>
    [JsonPropertyName("expiration_strategy")]
    public string? ExpirationStrategy { get; init; }

    /// <summary>Free text, no backing enum — see <see cref="PolicyStrategies"/>. Server default is <see cref="PolicyStrategies.FromExpiry"/>.</summary>
    [JsonPropertyName("renewal_basis")]
    public string? RenewalBasis { get; init; }

    /// <summary>
    /// Free text, no backing enum — see <see cref="PolicyStrategies"/> for the four well-known
    /// values. Server default is <see cref="PolicyStrategies.Token"/>. This is the field that
    /// decides whether this SDK's primary credential works at all.
    /// </summary>
    /// <remarks>
    /// The server accepts an <c>Authorization: License &lt;key&gt;</c> credential only when this is
    /// <see cref="PolicyStrategies.License"/> or <see cref="PolicyStrategies.Mixed"/>. Everything
    /// else — including <see cref="PolicyStrategies.Token"/>, the column default, and
    /// <see cref="PolicyStrategies.None"/>, which despite the name does not mean "auth is off" —
    /// answers <c>401 LICENSE_NOT_ALLOWED</c> (<see cref="LicenseNotAllowedException"/>). So
    /// license-key auth is off by default: a freshly created policy rejects it until someone
    /// changes this field. Retrying or re-issuing the key cannot fix it.
    /// </remarks>
    [JsonPropertyName("authentication_strategy")]
    public string? AuthenticationStrategy { get; init; }

    /// <summary>The key/checkout signing algorithm configured on this policy.</summary>
    [JsonPropertyName("scheme")]
    [JsonConverter(typeof(LicenseSchemeConverter))]
    public LicenseScheme Scheme { get; init; }

    /// <summary>Arbitrary key/value metadata attached to the policy.</summary>
    [JsonPropertyName("metadata")]
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; init; }
}
