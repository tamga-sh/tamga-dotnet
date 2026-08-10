namespace Tamga.Sdk.Tests;

// STUB — mirrors src/Tamga.Sdk/Transport.cs. No real tests yet. See
// docs/plans/tamga-dotnet.plan.md §B for intended coverage:
//   - each of the 5 auth transports produces the correct header/query
//     param for a given TamgaClientOptions
//   - Tamga-Version / Tamga-OTP header presence and sanitization (rejects
//     non-alphanumeric beyond `.`/`-`, rejects/truncates >32 chars)
//   - quick-validate response path deserializes without a `data` envelope,
//     while the two POST validate endpoints deserialize via
//     JsonApiDocument<T>
