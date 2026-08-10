namespace Tamga.Sdk.Tests.Models;

// STUB — mirrors src/Tamga.Sdk/Models/Policy.cs. No real tests yet. See
// docs/plans/tamga-dotnet.plan.md §C, §K for intended coverage:
//   - OverageStrategy all 5 variants round-trip
//   - OverageStrategy deserializes "DENY_ACCESS" without throwing and
//     resolves to NoOverage
//   - HeartbeatResurrectionStrategy deserializes "NO_RESURRECTION" without
//     throwing and resolves to NoRevive
//   - Policy tolerates a response payload missing max_memory/max_disk
//     entirely
//   - CheckInInterval round-trips all 4 lowercase values (regression
//     against accidentally uppercasing them)
