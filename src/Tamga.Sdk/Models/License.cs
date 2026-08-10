namespace Tamga.Sdk.Models;

// STUB — infrastructure scaffold only, no logic yet. See docs/plans/tamga-dotnet.plan.md §C.
//
// Intended contents:
//   - License record: id, key, suspended, expiry, uses, last_validated_at,
//     last_check_in_at, relationships to product/policy/user/environment.
//   - Scope record — all 8 meta.scope fields on the validate-by-ID endpoint:
//     Product, Policy, User, Environment (Guid?), Entitlements (string[]?),
//     Fingerprint, Version, Checksum (string?) — all optional, nulls
//     omitted on serialize.
//     GOTCHA: only Product/Policy/User/Environment are actually enforced
//     server-side today; Entitlements/Fingerprint/Version/Checksum are
//     parsed but silently ignored. Model them for forward-compatibility,
//     don't advertise them as functioning constraints yet.
//   - SkipTouch (bool, default false) — suppresses the last_validated_at
//     update side effect on the by-ID validate call.
//   - HasEntitlementAsync helper lives on TamgaClient (see Client.cs §J),
//     not here — this file only models the wire shapes.
