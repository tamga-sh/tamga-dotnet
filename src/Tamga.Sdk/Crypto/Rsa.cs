namespace Tamga.Sdk.Crypto;

// STUB — infrastructure scaffold only, no logic yet. See docs/plans/tamga-dotnet.plan.md §F, §H.
// ⚠ security-reviewer review MANDATORY before this file carries real logic.
//
// Intended contents:
//   bool VerifyPkcs1(RSA publicKey, ReadOnlySpan<byte> message,
//                     ReadOnlySpan<byte> signature)   // SHA-256
//   bool VerifyPss(RSA publicKey, ReadOnlySpan<byte> message,
//                   ReadOnlySpan<byte> signature)      // SHA-256
//   both via BCL System.Security.Cryptography.RSA — no third-party
//   dependency needed (available since .NET Core 3.0).
//
// Used by:
//   - Checkout/MachineFile.cs (§F) — PKCS1 and PSS branches of the
//     scheme-dispatched machine-file verifier.
//   - Proof.cs (§H) — offline proof verification is ALWAYS RSA-2048
//     PKCS#1 v1.5 / SHA-256, regardless of the license's LicenseScheme.
