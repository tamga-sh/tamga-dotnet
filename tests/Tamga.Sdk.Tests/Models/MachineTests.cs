namespace Tamga.Sdk.Tests.Models;

// STUB — mirrors src/Tamga.Sdk/Models/Machine.cs. No real tests yet. See
// docs/plans/tamga-dotnet.plan.md §G, §I for intended coverage:
//   - HeartbeatStatus round-trips all 4 values
//   - Pid round-trips as a JSON string, including numeric-looking values
//     (e.g. "1234"), even when constructed from an int
