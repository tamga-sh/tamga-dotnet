namespace Tamga.Sdk.Tests;

// STUB — mirrors src/Tamga.Sdk/Errors.cs. No real tests yet. See
// docs/plans/tamga-dotnet.plan.md §K for intended coverage:
//   - TamgaApiError parses a full JSON:API error envelope including
//     source.pointer
//   - each modeled `code` maps to its specific typed exception; an
//     unmodeled code falls back to TamgaApiException
