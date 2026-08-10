namespace Tamga.Sdk.Crypto;

// STUB — infrastructure scaffold only, no logic yet. See docs/plans/tamga-dotnet.plan.md §E.
// ⚠ security-reviewer review MANDATORY before this file carries real logic.
//
// Intended contents:
//   License-checkout encryption key derivation, replicated EXACTLY as the
//   server does it.
//
// ⚠ CRITICAL — this is NOT a KDF and NOT a hash. It is `license.key`'s raw
// UTF-8 bytes, zero-padded (keys shorter than 32 bytes) or truncated (keys
// longer than 32 bytes) to exactly 32 bytes. A verifier that hashes the key
// instead of zero-pad/truncating will silently fail to decrypt every
// encrypted `.lic` file. When this file gains a real implementation, flag
// the transform itself with an inline `// CRITICAL: not a KDF` comment at
// the call site, per the plan.
//
// Contrast with Crypto/Hkdf.cs (§F, machine checkout), which IS a real KDF
// (HKDF-SHA256) and additionally binds the machine's fingerprint into the
// derivation — the two paths are not interchangeable.
//
// Used by:
//   - Checkout/LicenseFile.cs (§E) — feeds Crypto/AesGcm.cs's key
//     parameter for the encrypted `.lic` file payload.
