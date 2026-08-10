namespace Tamga.Sdk.Crypto;

// STUB — infrastructure scaffold only, no logic yet. See docs/plans/tamga-dotnet.plan.md §F.
// ⚠ security-reviewer review MANDATORY before this file carries real logic.
//
// Intended contents:
//   bool Verify(ECDsa publicKey, ReadOnlySpan<byte> message,
//               ReadOnlySpan<byte> signature)   // P-256, SHA-256
//   via BCL System.Security.Cryptography.ECDsa — no third-party dependency
//   needed (available since .NET Core 3.0).
//
// Used by:
//   - Checkout/MachineFile.cs (§F) — the ECDSA-P256 branch of the
//     scheme-dispatched machine-file verifier.
