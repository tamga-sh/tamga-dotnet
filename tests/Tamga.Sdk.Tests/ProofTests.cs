namespace Tamga.Sdk.Tests;

// STUB — mirrors src/Tamga.Sdk/Proof.cs. No real tests yet. See
// docs/plans/tamga-dotnet.plan.md §H for intended coverage:
//   - canonical serializer produces byte-identical output to a known
//     server-generated fixture
//   - verification fails closed if `dataset` content is altered
//     post-signing
//   - explicit field-order regression test (same field set, different
//     order → verification must fail)
//   - empty dataset ({}) default round-trips correctly
//   - "v1x0." prefix parsing rejects malformed/missing-prefix strings
