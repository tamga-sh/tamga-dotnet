namespace Tamga.Sdk.Models;

// STUB — infrastructure scaffold only, no logic yet. See docs/plans/tamga-dotnet.plan.md §G, §I.
//
// Intended contents:
//   - Machine record: covers all create-optional fields (name, ip,
//     hostname, platform, cores, memory, disk, metadata) plus server-
//     returned heartbeat_status, last_heartbeat_at.
//   - HeartbeatStatus enum: NotStarted (NOT_STARTED) → Alive (ALIVE) →
//     Dead (DEAD) → Resurrected (RESURRECTED).
//     GOTCHA: the 600s (10 min) heartbeat window is hardcoded server-side,
//     NOT driven by policy.heartbeat_duration.
//   - Component record (§I): machine_id, fingerprint, name, metadata,
//     created, updated.
//   - Process record (§I): machine_id, pid, metadata, created, updated.
//     ⚠ Pid MUST be modeled as string, NOT int/long — the API types PIDs
//     as strings; coercing to a numeric type will reject or mangle
//     legitimate values.
//     GOTCHA: processes start Alive immediately (heartbeat timestamp set
//     at creation) — unlike machines, which start NotStarted. Process
//     heartbeat window is a hardcoded 30 seconds with NO resurrection
//     grace period (much tighter than the machine window).
