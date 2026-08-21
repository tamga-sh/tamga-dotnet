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

/// <summary>What happens to a machine row once it's been <c>DEAD</c> for longer than its resurrection grace window — <em>if</em> the cull job runs at all.</summary>
/// <remarks>
/// ⚠ This whole enum is inert unless <see cref="Policy.RequireHeartbeat"/> is <see langword="true"/>:
/// the server's cull job early-returns on a policy that does not require heartbeats, and that
/// column defaults to <c>FALSE</c>. Under a default policy no machine row is ever culled no matter
/// what this says, and <see cref="HeartbeatStatus.Dead"/> therefore never implies deletion.
/// </remarks>
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
/// Grace window after a machine is marked <c>DEAD</c> during which a new heartbeat ping is
/// recorded as a revival (transitions to <c>RESURRECTED</c>) rather than leaving the machine
/// exposed to <see cref="HeartbeatCullStrategy"/>.
/// </summary>
/// <remarks>
/// Note the ping itself is never gated on this window — it is a bare
/// <c>SET last_heartbeat_at = NOW()</c> and succeeds against a <c>DEAD</c> machine regardless, so
/// outside the grace window the machine simply comes back as <c>ALIVE</c> instead of
/// <c>RESURRECTED</c>. And the cull this window defers only runs at all when
/// <see cref="Policy.RequireHeartbeat"/> is <see langword="true"/>, which is not the default.
/// </remarks>
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
/// <remarks>
/// ⚠ The wire spelling is the ADVERBIAL form: <c>daily</c>/<c>weekly</c>/<c>monthly</c>/
/// <c>yearly</c>. That is what the column's own <c>CHECK</c> constraint permits, and it is the
/// only spelling a stored policy can hold. The noun forms <c>day</c>/<c>week</c>/<c>month</c>/
/// <c>year</c> are also decoded, defensively, because an earlier version of this SDK's
/// documentation claimed they were the wire values.
///
/// Read this together with <see cref="Policy.CheckInIntervalCount"/> — the interval is
/// <c>count × unit</c>, so this value alone is the period only when the count is 1.
/// </remarks>
[JsonConverter(typeof(CheckInIntervalConverter))]
public enum CheckInInterval
{
    /// <summary>Wire value <c>daily</c>.</summary>
    Day,

    /// <summary>Wire value <c>weekly</c>.</summary>
    Week,

    /// <summary>Wire value <c>monthly</c>.</summary>
    Month,

    /// <summary>Wire value <c>yearly</c>.</summary>
    Year,
}

/// <summary>Converts <see cref="CheckInInterval"/> to/from its lowercase wire string.</summary>
/// <remarks>
/// Both the adverbial wire spellings (<c>daily</c>…, what the server actually stores) and the noun
/// spellings (<c>day</c>…, which older SDK docs wrongly advertised) decode. Unrecognized values
/// fall back to <see cref="CheckInInterval.Day"/> — the shortest interval, so a policy this SDK
/// cannot read is over-served rather than under-served.
/// </remarks>
public sealed class CheckInIntervalConverter : JsonConverter<CheckInInterval>
{
    /// <summary>Deserializes the lowercase wire string into a <see cref="CheckInInterval"/>.</summary>
    public override CheckInInterval Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.GetString() switch
        {
            "weekly" or "week" => CheckInInterval.Week,
            "monthly" or "month" => CheckInInterval.Month,
            "yearly" or "year" => CheckInInterval.Year,
            _ => CheckInInterval.Day,
        };
    }

    /// <summary>Serializes the <see cref="CheckInInterval"/> as its lowercase wire string.</summary>
    public override void Write(Utf8JsonWriter writer, CheckInInterval value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            CheckInInterval.Week => "weekly",
            CheckInInterval.Month => "monthly",
            CheckInInterval.Year => "yearly",
            _ => "daily",
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
/// A license policy resource, flattened from the JSON:API <c>data.attributes</c> + <c>data.id</c>
/// shape by <see cref="FromResource"/>.
/// </summary>
/// <remarks>
/// <para>
/// The server's policy serializer emits exactly 30 attributes. Every one of them has a property
/// here; enumerate them against the serializer rather than trusting this sentence, and add the
/// missing one rather than shipping a model that silently drops it. This type spent its first
/// releases modelling 16 of the 30 — <c>product_id</c>, <c>name</c>, <c>duration</c>,
/// <c>strict</c>, <c>floating</c>, <c>encrypted</c>, <c>use_pool</c>, <c>protected</c>,
/// <c>check_in_interval_count</c>, <c>machine_uniqueness_strategy</c>, <c>expiration_basis</c>,
/// <c>max_users</c>, <c>created</c> and <c>updated</c> were all absent, which is the same defect
/// the licence model shipped with and the reason that list is written out here.
/// </para>
/// <para>
/// The two properties that go the other way — <see cref="MaxMemory"/> and <see cref="MaxDisk"/> —
/// are NOT on the server's response even though both are enforced during validation. The columns
/// exist and are read by the validator (<c>policies/model.rs:187-188</c>, used at <c>:302</c> and
/// <c>:309</c>), but <c>policies/serializer.rs</c> does not project them: it emits
/// <c>max_machines</c> and <c>max_cores</c> and stops (<c>:45-46</c>, <c>:83-84</c>). They are
/// therefore always <see langword="null"/> after <see cref="FromResource"/>, and the limits can
/// only be observed as <see cref="ValidationCode.TooMuchMemory"/>/
/// <see cref="ValidationCode.TooMuchDisk"/> on a failed validation.
/// </para>
/// <para>
/// They are nevertheless KEPT, and deliberately not <c>[Obsolete]</c>. Unlike the relationship ids
/// removed from <see cref="License"/> and <see cref="Machine"/> in 2.1.0 — which had no wire
/// binding at all and could not have been populated by any server — these two are ordinary
/// <c>[JsonPropertyName]</c> bindings on a type that is deserialized straight from
/// <c>attributes</c>. The day the serializer projects the columns it already has, they light up
/// with no SDK change. That is the same reason <see cref="MaxUsers"/> and
/// <c>HeartbeatScheduler.Dead</c> are kept: a member the server has not implemented yet is not a
/// deprecated member, and marking it <c>[Obsolete]</c> would fire <c>CS0618</c> in every consumer
/// build — an error, not a warning, under the <c>TreatWarningsAsErrors</c> this repo and many of
/// its consumers use — for a property that may become correct on its own.
/// </para>
/// <para>
/// No <c>relationships</c> object exists on this resource either (no serializer in the API emits
/// one), which is why <see cref="ProductId"/> is a plain attribute rather than a linkage.
/// </para>
/// </remarks>
public sealed record Policy
{
    /// <summary>The policy's unique identifier. Read from <c>data.id</c>, not from <c>attributes</c>.</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    /// <summary>The product this policy belongs to. A plain attribute — the resource carries no <c>relationships</c> object.</summary>
    [JsonPropertyName("product_id")]
    public Guid ProductId { get; init; }

    /// <summary>The policy's display name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>How long a license issued under this policy lasts, in seconds, or <see langword="null"/> for perpetual.</summary>
    [JsonPropertyName("duration")]
    public long? Duration { get; init; }

    /// <summary>Whether validation is strict (an unfulfilled requirement fails rather than warns).</summary>
    [JsonPropertyName("strict")]
    public bool Strict { get; init; }

    /// <summary>Whether one license may be shared across several machines concurrently.</summary>
    [JsonPropertyName("floating")]
    public bool Floating { get; init; }

    /// <summary>Whether licenses under this policy are issued encrypted.</summary>
    [JsonPropertyName("encrypted")]
    public bool Encrypted { get; init; }

    /// <summary>Whether license keys are drawn from a pre-generated pool instead of minted on creation.</summary>
    [JsonPropertyName("use_pool")]
    public bool UsePool { get; init; }

    /// <summary>Whether end users are blocked from self-deleting licenses under this policy.</summary>
    [JsonPropertyName("protected")]
    public bool Protected { get; init; }

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

    /// <summary>The maximum number of users allowed under this policy.</summary>
    /// <remarks>
    /// Modeled for completeness. <see cref="ValidationCode.TooManyUsers"/> has no construction site
    /// server-side, so exceeding this limit is not currently reported by validation.
    /// </remarks>
    [JsonPropertyName("max_users")]
    public int? MaxUsers { get; init; }

    /// <summary>
    /// The maximum memory allowed under this policy. Always <see langword="null"/> today: enforced
    /// server-side but never projected by the policy serializer — see type-level remarks for why
    /// this is modelled anyway rather than removed.
    /// </summary>
    [JsonPropertyName("max_memory")]
    public int? MaxMemory { get; init; }

    /// <summary>
    /// The maximum disk allowed under this policy. Always <see langword="null"/> today: enforced
    /// server-side but never projected by the policy serializer — see type-level remarks for why
    /// this is modelled anyway rather than removed.
    /// </summary>
    [JsonPropertyName("max_disk")]
    public int? MaxDisk { get; init; }

    /// <summary>Whether periodic check-in is required to keep a license valid.</summary>
    [JsonPropertyName("require_check_in")]
    public bool RequireCheckIn { get; init; }

    /// <summary>The required check-in interval, if <see cref="RequireCheckIn"/> is set.</summary>
    [JsonPropertyName("check_in_interval")]
    [JsonConverter(typeof(CheckInIntervalConverter))]
    public CheckInInterval? CheckInInterval { get; init; }

    /// <summary>
    /// Multiplier applied to <see cref="CheckInInterval"/> — e.g. <c>2</c> with
    /// <see cref="Models.CheckInInterval.Week"/> means "check in every two weeks".
    /// </summary>
    /// <remarks>
    /// Reading <see cref="CheckInInterval"/> without this gives the wrong period whenever the
    /// count is not 1.
    /// </remarks>
    [JsonPropertyName("check_in_interval_count")]
    public int? CheckInIntervalCount { get; init; }

    /// <summary>Whether machines must send periodic heartbeats to stay alive. Server default: <see langword="false"/>.</summary>
    /// <remarks>
    /// This is the master switch for dead-machine culling, and it is OFF by default. The server's
    /// cull job early-returns unless this is <see langword="true"/>, so on a default policy
    /// <see cref="HeartbeatCullStrategy"/> never fires and no machine row is ever removed for a
    /// missed heartbeat. Note that <c>heartbeat_status</c> is computed independently of this flag —
    /// a machine on a default policy still reports <see cref="HeartbeatStatus.Dead"/> once its
    /// window lapses, indefinitely, while its row and seat stay put.
    /// </remarks>
    [JsonPropertyName("require_heartbeat")]
    public bool RequireHeartbeat { get; init; }

    /// <summary>
    /// The heartbeat window, in seconds. This DOES drive the server's window: when set it is used
    /// as-is, and 600s applies only as the fallback when it is null.
    /// </summary>
    /// <remarks>
    /// An earlier version of this comment said the window was hardcoded to 600s and that this
    /// field was ignored. That was wrong.
    /// <c>Policy::effective_heartbeat_duration_secs</c> returns this value when set, the culling
    /// job measures against <c>COALESCE(p.heartbeat_duration, 600)</c>, and
    /// <c>heartbeat_status</c>/<c>next_heartbeat_at</c> are derived from the same window.
    ///
    /// Using this to size a client-side ping interval is correct, and there are now two ways to
    /// get at it: <see cref="TamgaClient.GetLicensePolicyAsync"/> reads it straight off the
    /// governing policy, and a checked-out <c>.machine</c> file still yields the same window as
    /// <c>NextHeartbeatAt - LastHeartbeatAt</c> (see <see cref="Machine.NextHeartbeatAt"/>) for
    /// callers who cannot reach the policy route. <see cref="EffectiveHeartbeatDurationSeconds"/>
    /// applies the server's own fallback so a caller never has to remember which of the two cases
    /// they are in.
    ///
    /// <see cref="HeartbeatScheduler.DefaultInterval"/> is still computed from the 600s fallback
    /// and still does not adapt on its own — a scheduler sized from a policy has to be constructed
    /// with the interval, e.g. from
    /// <see cref="TamgaClient.GetHeartbeatIntervalAsync(Guid, CancellationToken)"/>.
    /// </remarks>
    [JsonPropertyName("heartbeat_duration")]
    public int? HeartbeatDuration { get; init; }

    /// <summary>
    /// The heartbeat window actually in force, in seconds: <see cref="HeartbeatDuration"/> when the
    /// policy sets it, otherwise the server's 600s fallback.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>Policy::effective_heartbeat_duration_secs</c>
    /// (<c>heartbeat_duration.map(i64::from).unwrap_or(600)</c>) exactly, which is the same value
    /// the culling job measures against via <c>COALESCE(p.heartbeat_duration, 600)</c>. Prefer this
    /// over reading <see cref="HeartbeatDuration"/> directly — a <see langword="null"/> there does
    /// not mean "no window".
    /// </remarks>
    public int EffectiveHeartbeatDurationSeconds =>
        HeartbeatDuration ?? HeartbeatScheduler.ServerHeartbeatWindowSeconds;

    /// <summary><see cref="EffectiveHeartbeatDurationSeconds"/> as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan EffectiveHeartbeatWindow => TimeSpan.FromSeconds(EffectiveHeartbeatDurationSeconds);

    /// <summary>How overage beyond the policy's <c>max_*</c> limits is tolerated.</summary>
    [JsonPropertyName("overage_strategy")]
    [JsonConverter(typeof(OverageStrategyConverter))]
    public OverageStrategy OverageStrategy { get; init; }

    /// <summary>What happens to a machine row once it's been dead for longer than its resurrection grace window — only if <see cref="RequireHeartbeat"/> is <see langword="true"/>, which it is not by default.</summary>
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

    /// <summary>
    /// When a new license's expiry clock starts. Free text; the server's own constraint allows
    /// <c>FROM_CREATION</c> (the default), <c>FROM_FIRST_VALIDATION</c>,
    /// <c>FROM_FIRST_ACTIVATION</c>, <c>FROM_FIRST_DOWNLOAD</c> and <c>FROM_FIRST_USE</c>.
    /// </summary>
    [JsonPropertyName("expiration_basis")]
    public string? ExpirationBasis { get; init; }

    /// <summary>
    /// The scope a machine fingerprint must be unique within. Free text; the server's own
    /// constraint allows <c>UNIQUE_PER_LICENSE</c> (the default), <c>UNIQUE_PER_POLICY</c> and
    /// <c>UNIQUE_PER_ACCOUNT</c>.
    /// </summary>
    /// <remarks>
    /// Worth reading before interpreting a <see cref="FingerprintTakenException"/>: under
    /// <c>UNIQUE_PER_POLICY</c> or <c>UNIQUE_PER_ACCOUNT</c> the machine that already holds the
    /// fingerprint may belong to a DIFFERENT license, so "already activated, carry on" is not a
    /// safe reading of the 409 on those policies. See
    /// <see cref="TamgaClient.ActivateMachineIdempotentAsync"/>.
    /// </remarks>
    [JsonPropertyName("machine_uniqueness_strategy")]
    public string? MachineUniquenessStrategy { get; init; }

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

    /// <summary>When the policy was created.</summary>
    [JsonPropertyName("created")]
    public DateTimeOffset? Created { get; init; }

    /// <summary>When the policy was last updated.</summary>
    [JsonPropertyName("updated")]
    public DateTimeOffset? Updated { get; init; }

    /// <summary>
    /// Flattens a raw JSON:API policy resource into a <see cref="Policy"/>, taking
    /// <see cref="Id"/> from <c>data.id</c> and everything else from <c>data.attributes</c>.
    /// </summary>
    /// <remarks>
    /// This type doubles as its own attributes bag: <c>attributes</c> deserializes straight into a
    /// <see cref="Policy"/> (leaving <see cref="Id"/> at its default, since <c>id</c> lives one
    /// level up) and the id is grafted on here. That keeps one property list instead of two that
    /// can drift apart — which is exactly how attributes went missing from this model before.
    /// </remarks>
    /// <param name="resource">The JSON:API resource object to flatten.</param>
    public static Policy FromResource(JsonApiResource<Policy> resource) =>
        (resource.Attributes ?? new Policy()) with { Id = resource.Id };
}
