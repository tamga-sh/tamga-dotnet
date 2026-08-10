namespace Tamga.Sdk.Models;

// STUB — infrastructure scaffold only, no logic yet. See docs/plans/tamga-dotnet.plan.md §C, §F, §K.
//
// Intended contents:
//   - LicenseScheme enum (key/checkout signing algorithm): Ed25519Sign
//     (ED25519_SIGN), Rsa2048Pkcs1Sign (RSA_2048_PKCS1_SIGN),
//     Rsa2048Pkcs1PssSign (RSA_2048_PKCS1_PSS_SIGN),
//     EcdsaP256Sign (ECDSA_P256_SIGN), Rsa2048JwtRs256 (RSA_2048_JWT_RS256),
//     plus None/unset = legacy plain key string, unsigned.
//   - OverageStrategy enum: NoOverage (NO_OVERAGE, x1),
//     Allow125xOverage (ALLOW_1_25X_OVERAGE),
//     Allow15xOverage (ALLOW_1_5X_OVERAGE),
//     Allow2xOverage (ALLOW_2X_OVERAGE),
//     AlwaysAllowOverage (ALWAYS_ALLOW_OVERAGE, limit ignored).
//     Applies to machines/cores/memory/disk/processes, NOT `uses` (strict
//     >= regardless of strategy).
//   - HeartbeatCullStrategy enum: DeactivateDead (DEACTIVATE_DEAD, row
//     deleted), KeepDead (KEEP_DEAD, row kept).
//   - HeartbeatResurrectionStrategy enum: NoRevive (NO_REVIVE),
//     OneMinuteRevive, TwoMinuteRevive, FiveMinuteRevive, TenMinuteRevive,
//     FifteenMinuteRevive (*_MINUTE_REVIVE), AlwaysRevive (ALWAYS_REVIVE).
//   - CheckInInterval enum — lowercase wire values (inconsistent with the
//     SCREAMING_SNAKE_CASE convention used elsewhere): "day", "week",
//     "month", "year".
//   - Free-text fields with no backing server enum (model as string-typed
//     properties plus well-known-constant helpers, not a closed C# enum):
//     ExpirationStrategy ("RESTRICT_ACCESS" default;
//     "MAINTAIN_ACCESS"/"ALLOW_ACCESS" permit post-expiry access),
//     RenewalBasis ("FROM_EXPIRY" default vs "FROM_NOW"),
//     AuthenticationStrategy ("TOKEN" default; "LICENSE"/"MIXED" permit
//     license-key bearer auth).
//   - ⚠ CRITICAL: OverageStrategy/HeartbeatResurrectionStrategy
//     deserialization must handle two non-real default strings a freshly
//     created policy can return: "DENY_ACCESS" (not a real OverageStrategy
//     variant — server silently treats it as NoOverage) and
//     "NO_RESURRECTION" (not a real HeartbeatResurrectionStrategy variant —
//     server silently treats it as NoRevive). The custom JsonConverter for
//     both enums must deserialize these two strings without throwing AND
//     must NOT invent a fake DenyAccess/NoResurrection C# enum member that
//     implies restrictive behavior the server doesn't actually apply — map
//     them directly to NoOverage/NoRevive.
//   - Policy record — full attribute set: MaxMachines, MaxCores,
//     MaxProcesses, MaxUses, RequireCheckIn, HeartbeatDuration,
//     OverageStrategy, HeartbeatCullStrategy, HeartbeatResurrectionStrategy,
//     ExpirationStrategy, RenewalBasis, AuthenticationStrategy,
//     CheckInInterval, Scheme.
//     GOTCHA: the policy GET response omits max_memory and max_disk even
//     though both are enforced during validation — model as nullable
//     (int?), not required; the SDK cannot introspect these two limits,
//     only observe TooMuchMemory/TooMuchDisk on validation failure.
