namespace Tamga.Sdk.Crypto;

// STUB — infrastructure scaffold only, no logic yet. See docs/plans/tamga-dotnet.plan.md §E, §F.
// ⚠ security-reviewer review MANDATORY before this file carries real logic.
//
// Intended contents:
//   Open/seal wrapper over BCL System.Security.Cryptography.AesGcm — no
//   third-party dependency needed (available since .NET Core 3.0).
//
// Used by:
//   - Checkout/LicenseFile.cs (§E) — encrypted `.lic` payload:
//     base64-decoded `enc` splits into nonce(12B) ‖ ciphertext ‖ tag(16B);
//     key comes from Crypto/NaiveKey.cs (NOT this file's concern).
//   - Checkout/MachineFile.cs (§F) — same envelope shape, key comes from
//     Crypto/Hkdf.cs instead of NaiveKey.
//
// This file is algorithm-only: it never derives the AES key itself — see
// NaiveKey.cs (license checkout) and Hkdf.cs (machine checkout) for the two
// distinct, non-interchangeable key-derivation paths that feed it.
