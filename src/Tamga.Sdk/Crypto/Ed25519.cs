namespace Tamga.Sdk.Crypto;

// STUB — infrastructure scaffold only, no logic yet. See docs/plans/tamga-dotnet.plan.md §E, §F.
// ⚠ security-reviewer review MANDATORY before this file carries real logic.
//
// Intended contents:
//   bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message,
//               ReadOnlySpan<byte> signature)
//   backed by NSec.Cryptography.SignatureAlgorithm.Ed25519.
//
// GOTCHA (see CLAUDE.md "Cryptography Backend"): System.Security.Cryptography
// gets native Ed25519 only in .NET 9+. Since this SDK targets net8.0 only
// for v0.1, this file MUST use NSec.Cryptography (a libsodium binding) —
// do not "simplify" by reaching for a BCL Ed25519 type; it does not exist
// on net8.0 and the build will not compile against it.
//
// Used by:
//   - Checkout/LicenseFile.cs (§E) — Ed25519 is the ONLY signature scheme
//     for license checkout files, independent of the license's own
//     LicenseScheme.
//   - Checkout/MachineFile.cs (§F) — the Ed25519 branch of the
//     scheme-dispatched machine-file verifier.
