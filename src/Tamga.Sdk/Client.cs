namespace Tamga.Sdk;

// STUB — infrastructure scaffold only, no logic yet. See docs/plans/tamga-dotnet.plan.md §B–K.
//
// Intended contents:
//   TamgaClient — the SDK's single public entry point. Constructor overloads:
//   options-only, and options + externally-injected HttpClient
//   (IHttpClientFactory-friendly).
//
//   Public async methods (one Task<T>-returning method per endpoint, every
//   call taking a trailing CancellationToken), grouped by protocol section:
//
//     §C License Validation
//       ValidateByKeyAsync(string key, CancellationToken)
//       ValidateByIdAsync(Guid licenseId, Scope? scope, bool skipTouch, CancellationToken)
//       QuickValidateAsync(Guid licenseId, CancellationToken)
//
//     §D License Check-In
//       CheckInAsync(Guid licenseId, CancellationToken)
//
//     §E License Checkout (⚠ security-reviewer MANDATORY — see Checkout/LicenseFile.cs)
//       CheckOutLicenseAsync(Guid licenseId, bool encrypt, int? ttl, CancellationToken)
//
//     §F Machine Checkout (⚠ security-reviewer MANDATORY — see Checkout/MachineFile.cs)
//       CheckOutMachineAsync(Guid machineId, bool encrypt, int? ttl, CancellationToken)
//
//     §G Machine Management
//       CreateMachineAsync(...), ActivateMachineAsync(...) convenience helper
//       (create → validate → interpret over-limit codes → optional delete),
//       PingHeartbeatAsync(Guid machineId, CancellationToken)
//       ResetHeartbeatAsync(Guid machineId, CancellationToken)
//
//     §H Machine Offline Proof (⚠ security-reviewer MANDATORY — see Proof.cs)
//       GenerateOfflineProofAsync(Guid machineId, JsonNode? dataset, CancellationToken)
//
//     §I Components & Processes
//       CreateComponentAsync(...), ListComponentsAsync(...)
//       CreateProcessAsync(...), PingProcessAsync(Guid processId, CancellationToken)
//
//     §J Entitlements
//       ListEntitlementsAsync(...), GetEntitlementAsync(...)
//       HasEntitlementAsync(Guid licenseId, string code, CancellationToken) —
//       matches on `code`, NOT `name` (see Models/Entitlement.cs).
//
//   Doc-comment reminders to carry into the real implementation:
//     - Auth is currently NOT enforced server-side on the 3 validate
//       endpoints, but always send Authorization: License <key> anyway
//       (forward-compatible once enforcement lands).
//     - No machine/core/etc. limit is checked at machine creation time —
//       those limits only surface later via license validation.
