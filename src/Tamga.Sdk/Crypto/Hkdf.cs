namespace Tamga.Sdk.Crypto;

// STUB — infrastructure scaffold only, no logic yet. See docs/plans/tamga-dotnet.plan.md §F.
// ⚠ security-reviewer review MANDATORY before this file carries real logic.
//
// Intended contents:
//   HKDF-SHA256 wrapper via BCL System.Security.Cryptography.HKDF — no
//   third-party dependency needed (available since .NET Core 3.0).
//   Produces a 32-byte AES key from:
//     salt = "tamga:machine-file-key-v1"
//     ikm  = <license key>
//     info = <machine fingerprint>
//
// GOTCHA: this is the machine-checkout key derivation and is a REAL KDF —
// it is explicitly NOT the same scheme as license checkout's naive
// derivation (see NaiveKey.cs). Machine file decryption requires BOTH the
// license key AND the target machine's fingerprint; license file decryption
// requires only the license key. Do not conflate the two paths.
//
// Used by:
//   - Checkout/MachineFile.cs (§F) — feeds Crypto/AesGcm.cs's key parameter
//     for the encrypted `.machine` file payload.
