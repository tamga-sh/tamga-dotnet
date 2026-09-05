using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamga.Sdk.Models;

/// <summary>
/// Mirrors <c>meta.code</c> on the license validate endpoints. Models all 24 wire values; 19 are
/// reachable today and 5 are not — <see cref="NotFound"/>, <see cref="Banned"/>,
/// <see cref="ComponentsScopeMismatch"/>, <see cref="ChecksumScopeMismatch"/>,
/// <see cref="VersionScopeMismatch"/> (see per-member remarks). An unrecognized/future wire value
/// deserializes to <see cref="Unknown"/> rather than throwing (see <see cref="ValidationCodeConverter"/>).
/// </summary>
[JsonConverter(typeof(ValidationCodeConverter))]
public enum ValidationCode
{
    /// <summary>Fallback for any wire value not modeled below (including future server additions). Not a real server value itself.</summary>
    Unknown,

    /// <summary>Wire value <c>VALID</c>. ✅ reachable: all checks passed.</summary>
    Valid,

    /// <summary>Wire value <c>SUSPENDED</c>. ✅ reachable: <c>license.suspended == true</c>.</summary>
    Suspended,

    /// <summary>Wire value <c>EXPIRED</c>. ✅ reachable: <c>expiry &lt; now</c>.</summary>
    Expired,

    /// <summary>Wire value <c>OVERDUE</c>. ✅ reachable: check-in required and window elapsed.</summary>
    Overdue,

    /// <summary>Wire value <c>PRODUCT_SCOPE_MISMATCH</c>. ✅ reachable.</summary>
    ProductScopeMismatch,

    /// <summary>Wire value <c>POLICY_SCOPE_MISMATCH</c>. ✅ reachable.</summary>
    PolicyScopeMismatch,

    /// <summary>Wire value <c>USER_SCOPE_MISMATCH</c>. ✅ reachable.</summary>
    UserScopeMismatch,

    /// <summary>Wire value <c>ENVIRONMENT_SCOPE_MISMATCH</c>. ✅ reachable.</summary>
    EnvironmentScopeMismatch,

    /// <summary>Wire value <c>TOO_MANY_MACHINES</c>. ✅ reachable, per overage strategy.</summary>
    TooManyMachines,

    /// <summary>Wire value <c>TOO_MANY_CORES</c>. ✅ reachable, per overage strategy.</summary>
    TooManyCores,

    /// <summary>Wire value <c>TOO_MUCH_MEMORY</c>. ✅ reachable, per overage strategy.</summary>
    TooMuchMemory,

    /// <summary>Wire value <c>TOO_MUCH_DISK</c>. ✅ reachable, per overage strategy.</summary>
    TooMuchDisk,

    /// <summary>Wire value <c>TOO_MANY_PROCESSES</c>. ✅ reachable, per overage strategy.</summary>
    TooManyProcesses,

    /// <summary>Wire value <c>TOO_MANY_USES</c>. ✅ reachable, strict <c>&gt;=</c>, no overage strategy applies.</summary>
    TooManyUses,

    /// <summary>Wire value <c>NOT_FOUND</c>. ⛔ handler returns HTTP 404 directly instead; this <c>meta.code</c> value never actually appears.</summary>
    NotFound,

    /// <summary>Wire value <c>BANNED</c>. ⛔ declared, never emitted.</summary>
    Banned,

    /// <summary>
    /// Wire value <c>ENTITLEMENTS_MISSING</c>. ✅ reachable: <see cref="Scope.Entitlements"/> named
    /// at least one entitlement code the license does not effectively hold.
    /// </summary>
    EntitlementsMissing,

    /// <summary>Wire value <c>TOO_MANY_USERS</c>. ✅ reachable: emitted by all three validate endpoints when the license's user count exceeds <c>policy.max_users</c>. Never part of an activation rollback set — activation creates no users.</summary>
    TooManyUsers,

    /// <summary>Wire value <c>HEARTBEAT_DEAD</c>. ✅ reachable: <see cref="Scope.Fingerprint"/> named a machine on the license whose heartbeat status is <c>DEAD</c> and the policy sets <c>require_heartbeat</c>. Not emitted without a fingerprint scope, and never by a heartbeat route.</summary>
    HeartbeatDead,

    /// <summary>Wire value <c>HEARTBEAT_NOT_STARTED</c>. ✅ reachable: <see cref="Scope.Fingerprint"/> named a machine on the license that has never pinged (<c>NOT_STARTED</c>) and the policy sets <c>require_heartbeat</c>. Not emitted without a fingerprint scope.</summary>
    HeartbeatNotStarted,

    /// <summary>
    /// Wire value <c>FINGERPRINT_SCOPE_MISMATCH</c>. ✅ reachable: <see cref="Scope.Fingerprint"/>
    /// did not match any machine activated against the license.
    /// </summary>
    FingerprintScopeMismatch,

    /// <summary>Wire value <c>COMPONENTS_SCOPE_MISMATCH</c>. ⛔ declared, never emitted.</summary>
    ComponentsScopeMismatch,

    /// <summary>Wire value <c>CHECKSUM_SCOPE_MISMATCH</c>. ⛔ unreachable: sending <c>scope.checksum</c> now fails the whole call with <c>422 SCOPE_NOT_SUPPORTED</c> instead, so this code is never reached.</summary>
    ChecksumScopeMismatch,

    /// <summary>Wire value <c>VERSION_SCOPE_MISMATCH</c>. ⛔ unreachable: sending <c>scope.version</c> now fails the whole call with <c>422 SCOPE_NOT_SUPPORTED</c> instead, so this code is never reached.</summary>
    VersionScopeMismatch,
}

/// <summary>
/// Converts <see cref="ValidationCode"/> to/from its wire string, falling back to
/// <see cref="ValidationCode.Unknown"/> for any unrecognized value instead of throwing — required
/// so a future server-side addition to the 24-value enum doesn't hard-fail deserialization for
/// SDK consumers who haven't upgraded yet.
/// </summary>
public sealed class ValidationCodeConverter : JsonConverter<ValidationCode>
{
    /// <summary>Deserializes the wire string into a <see cref="ValidationCode"/>.</summary>
    public override ValidationCode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value switch
        {
            "VALID" => ValidationCode.Valid,
            "SUSPENDED" => ValidationCode.Suspended,
            "EXPIRED" => ValidationCode.Expired,
            "OVERDUE" => ValidationCode.Overdue,
            "PRODUCT_SCOPE_MISMATCH" => ValidationCode.ProductScopeMismatch,
            "POLICY_SCOPE_MISMATCH" => ValidationCode.PolicyScopeMismatch,
            "USER_SCOPE_MISMATCH" => ValidationCode.UserScopeMismatch,
            "ENVIRONMENT_SCOPE_MISMATCH" => ValidationCode.EnvironmentScopeMismatch,
            "TOO_MANY_MACHINES" => ValidationCode.TooManyMachines,
            "TOO_MANY_CORES" => ValidationCode.TooManyCores,
            "TOO_MUCH_MEMORY" => ValidationCode.TooMuchMemory,
            "TOO_MUCH_DISK" => ValidationCode.TooMuchDisk,
            "TOO_MANY_PROCESSES" => ValidationCode.TooManyProcesses,
            "TOO_MANY_USES" => ValidationCode.TooManyUses,
            "NOT_FOUND" => ValidationCode.NotFound,
            "BANNED" => ValidationCode.Banned,
            "ENTITLEMENTS_MISSING" => ValidationCode.EntitlementsMissing,
            "TOO_MANY_USERS" => ValidationCode.TooManyUsers,
            "HEARTBEAT_DEAD" => ValidationCode.HeartbeatDead,
            "HEARTBEAT_NOT_STARTED" => ValidationCode.HeartbeatNotStarted,
            "FINGERPRINT_SCOPE_MISMATCH" => ValidationCode.FingerprintScopeMismatch,
            "COMPONENTS_SCOPE_MISMATCH" => ValidationCode.ComponentsScopeMismatch,
            "CHECKSUM_SCOPE_MISMATCH" => ValidationCode.ChecksumScopeMismatch,
            "VERSION_SCOPE_MISMATCH" => ValidationCode.VersionScopeMismatch,
            _ => ValidationCode.Unknown,
        };
    }

    /// <summary>Serializes the <see cref="ValidationCode"/> as its wire string.</summary>
    public override void Write(Utf8JsonWriter writer, ValidationCode value, JsonSerializerOptions options)
        => writer.WriteStringValue(ToWireString(value));

    /// <summary>
    /// The wire string for a <see cref="ValidationCode"/> — the one place the enum-to-string
    /// direction is spelled out, shared by <see cref="Write"/> and by the client-side
    /// <see cref="MachineOverLimitException"/>, which needs the value the server actually sent
    /// in <c>meta.code</c> without a serializer round-trip.
    /// </summary>
    internal static string ToWireString(ValidationCode value) => value switch
    {
        ValidationCode.Valid => "VALID",
        ValidationCode.Suspended => "SUSPENDED",
        ValidationCode.Expired => "EXPIRED",
        ValidationCode.Overdue => "OVERDUE",
        ValidationCode.ProductScopeMismatch => "PRODUCT_SCOPE_MISMATCH",
        ValidationCode.PolicyScopeMismatch => "POLICY_SCOPE_MISMATCH",
        ValidationCode.UserScopeMismatch => "USER_SCOPE_MISMATCH",
        ValidationCode.EnvironmentScopeMismatch => "ENVIRONMENT_SCOPE_MISMATCH",
        ValidationCode.TooManyMachines => "TOO_MANY_MACHINES",
        ValidationCode.TooManyCores => "TOO_MANY_CORES",
        ValidationCode.TooMuchMemory => "TOO_MUCH_MEMORY",
        ValidationCode.TooMuchDisk => "TOO_MUCH_DISK",
        ValidationCode.TooManyProcesses => "TOO_MANY_PROCESSES",
        ValidationCode.TooManyUses => "TOO_MANY_USES",
        ValidationCode.NotFound => "NOT_FOUND",
        ValidationCode.Banned => "BANNED",
        ValidationCode.EntitlementsMissing => "ENTITLEMENTS_MISSING",
        ValidationCode.TooManyUsers => "TOO_MANY_USERS",
        ValidationCode.HeartbeatDead => "HEARTBEAT_DEAD",
        ValidationCode.HeartbeatNotStarted => "HEARTBEAT_NOT_STARTED",
        ValidationCode.FingerprintScopeMismatch => "FINGERPRINT_SCOPE_MISMATCH",
        ValidationCode.ComponentsScopeMismatch => "COMPONENTS_SCOPE_MISMATCH",
        ValidationCode.ChecksumScopeMismatch => "CHECKSUM_SCOPE_MISMATCH",
        ValidationCode.VersionScopeMismatch => "VERSION_SCOPE_MISMATCH",
        _ => "UNKNOWN",
    };
}
