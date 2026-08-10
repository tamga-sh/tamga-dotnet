namespace Tamga.Sdk.Models;

// STUB — infrastructure scaffold only, no logic yet. See docs/plans/tamga-dotnet.plan.md §C.
//
// Intended contents:
//   ValidationCode — 24-value enum mirroring `meta.code` on the license
//   validate-by-ID endpoint, with a [JsonConverter] that deserializes
//   unknown/future wire values leniently (fallback case) rather than
//   throwing. Model all 24; only the ✅ ones are reachable server-side today
//   (docs/sdk.md §2) — don't build UX around the ⛔ ones.
//
//   ✅ reachable today:
//     Valid (VALID), Suspended (SUSPENDED), Expired (EXPIRED),
//     Overdue (OVERDUE), ProductScopeMismatch (PRODUCT_SCOPE_MISMATCH),
//     PolicyScopeMismatch (POLICY_SCOPE_MISMATCH),
//     UserScopeMismatch (USER_SCOPE_MISMATCH),
//     EnvironmentScopeMismatch (ENVIRONMENT_SCOPE_MISMATCH),
//     TooManyMachines (TOO_MANY_MACHINES), TooManyCores (TOO_MANY_CORES),
//     TooMuchMemory (TOO_MUCH_MEMORY), TooMuchDisk (TOO_MUCH_DISK),
//     TooManyProcesses (TOO_MANY_PROCESSES), TooManyUses (TOO_MANY_USES)
//
//   ⛔ declared but never emitted (NotFound: handler returns HTTP 404
//   directly instead, so this meta.code value never actually appears):
//     NotFound (NOT_FOUND), Banned (BANNED),
//     EntitlementsMissing (ENTITLEMENTS_MISSING),
//     TooManyUsers (TOO_MANY_USERS), HeartbeatDead (HEARTBEAT_DEAD),
//     HeartbeatNotStarted (HEARTBEAT_NOT_STARTED),
//     FingerprintScopeMismatch (FINGERPRINT_SCOPE_MISMATCH),
//     ComponentsScopeMismatch (COMPONENTS_SCOPE_MISMATCH),
//     ChecksumScopeMismatch (CHECKSUM_SCOPE_MISMATCH),
//     VersionScopeMismatch (VERSION_SCOPE_MISMATCH)
