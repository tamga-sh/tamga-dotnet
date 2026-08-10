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
/// silently treats it as <see cref="OverageStrategy.NoOverage"/> (see gap #9 in
/// <c>tamga-api/docs/sdk.md</c>). This converter must decode that string to
/// <see cref="OverageStrategy.NoOverage"/> without throwing, and must NOT invent a fake
/// <c>DenyAccess</c> member that implies restrictive behavior the server doesn't actually apply.
/// Any other unrecognized string also falls back to <see cref="OverageStrategy.NoOverage"/>
/// rather than throwing, matching the server's own fallback (see
/// <c>PolicyAttributes::overage_strategy_parsed</c> in <c>tamga-api</c>).
/// </remarks>
public sealed class OverageStrategyConverter : JsonConverter<OverageStrategy>
{
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
    public override HeartbeatCullStrategy Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.GetString() switch
        {
            "KEEP_DEAD" => HeartbeatCullStrategy.KeepDead,
            _ => HeartbeatCullStrategy.DeactivateDead,
        };
    }

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
    public const string RestrictAccess = "RESTRICT_ACCESS";
    public const string MaintainAccess = "MAINTAIN_ACCESS";
    public const string AllowAccess = "ALLOW_ACCESS";

    public const string FromExpiry = "FROM_EXPIRY";
    public const string FromNow = "FROM_NOW";

    public const string Token = "TOKEN";
    public const string License = "LICENSE";
    public const string Mixed = "MIXED";
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
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("max_machines")]
    public int? MaxMachines { get; init; }

    [JsonPropertyName("max_cores")]
    public int? MaxCores { get; init; }

    [JsonPropertyName("max_processes")]
    public int? MaxProcesses { get; init; }

    [JsonPropertyName("max_uses")]
    public int? MaxUses { get; init; }

    /// <summary>Not present on the server's GET response even though it is enforced — see type-level remarks.</summary>
    [JsonPropertyName("max_memory")]
    public int? MaxMemory { get; init; }

    /// <summary>Not present on the server's GET response even though it is enforced — see type-level remarks.</summary>
    [JsonPropertyName("max_disk")]
    public int? MaxDisk { get; init; }

    [JsonPropertyName("require_check_in")]
    public bool RequireCheckIn { get; init; }

    [JsonPropertyName("check_in_interval")]
    [JsonConverter(typeof(CheckInIntervalConverter))]
    public CheckInInterval? CheckInInterval { get; init; }

    [JsonPropertyName("require_heartbeat")]
    public bool RequireHeartbeat { get; init; }

    /// <summary>
    /// GOTCHA: not actually used to compute the server's heartbeat window — that window is
    /// hardcoded to 600s regardless of this value (see gap #8). Do not use this to compute a
    /// client-side heartbeat scheduler interval.
    /// </summary>
    [JsonPropertyName("heartbeat_duration")]
    public int? HeartbeatDuration { get; init; }

    [JsonPropertyName("overage_strategy")]
    [JsonConverter(typeof(OverageStrategyConverter))]
    public OverageStrategy OverageStrategy { get; init; }

    [JsonPropertyName("heartbeat_cull_strategy")]
    [JsonConverter(typeof(HeartbeatCullStrategyConverter))]
    public HeartbeatCullStrategy HeartbeatCullStrategy { get; init; }

    [JsonPropertyName("heartbeat_resurrection_strategy")]
    [JsonConverter(typeof(HeartbeatResurrectionStrategyConverter))]
    public HeartbeatResurrectionStrategy HeartbeatResurrectionStrategy { get; init; }

    /// <summary>Free text, no backing enum — see <see cref="PolicyStrategies"/> for well-known values. Server default is <see cref="PolicyStrategies.RestrictAccess"/>.</summary>
    [JsonPropertyName("expiration_strategy")]
    public string? ExpirationStrategy { get; init; }

    /// <summary>Free text, no backing enum — see <see cref="PolicyStrategies"/>. Server default is <see cref="PolicyStrategies.FromExpiry"/>.</summary>
    [JsonPropertyName("renewal_basis")]
    public string? RenewalBasis { get; init; }

    /// <summary>Free text, no backing enum — see <see cref="PolicyStrategies"/>. Server default is <see cref="PolicyStrategies.Token"/>.</summary>
    [JsonPropertyName("authentication_strategy")]
    public string? AuthenticationStrategy { get; init; }

    [JsonPropertyName("scheme")]
    [JsonConverter(typeof(LicenseSchemeConverter))]
    public LicenseScheme Scheme { get; init; }

    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }
}
