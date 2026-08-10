namespace Tamga.Sdk.Checkout;

// STUB — infrastructure scaffold only, no logic yet. See docs/plans/tamga-dotnet.plan.md §F.
// ⚠ security-reviewer review MANDATORY before this file carries real logic.
//
// Intended contents:
//   PEM wrapper: -----BEGIN MACHINE FILE----- / -----END MACHINE FILE-----,
//   same inner {enc, sig, alg} JSON structure as Checkout/LicenseFile.cs.
//
//   GOTCHA: signing scheme is taken from the LICENSE's `scheme` field
//   (Models/Policy.cs LicenseScheme), NOT hardcoded Ed25519 like license
//   checkout (§E). Verify dispatch selects Ed25519 / RSA-PKCS1 / RSA-PSS /
//   ECDSA-P256 based on that value, reusing Crypto/Ed25519.cs for the
//   Ed25519 case.
//
//   ⚠ `RSA_2048_JWT_RS256` is explicitly rejected server-side for machine
//   files (422 SCHEME_NOT_SUPPORTED) — this SDK's verifier must NOT
//   implement or attempt JWT/RS256 verification for machine files; an
//   incoming RSA_2048_JWT_RS256-scheme machine file must throw
//   SchemeNotSupportedException client-side too, not silently no-op.
//
//   Encryption key derivation is HKDF-SHA256 (Crypto/Hkdf.cs) — NOT the
//   naive zero-pad/truncate scheme used by license checkout
//   (Crypto/NaiveKey.cs). Decryption requires BOTH the license key AND the
//   target machine's fingerprint. Encrypted-payload path reuses
//   Crypto/AesGcm.cs with the HKDF-derived key.
//
//   GOTCHA: `ttl` is server-validated > 0 and <= 31536000 (365 days) —
//   validate client-side too, to fail fast, in addition to handling the
//   server's 422 TTL_INVALID.
//
//   Public API:
//     MachineFile.Verify(LicenseScheme scheme, byte[] publicKey)
//     MachineFile.VerifyAndDecrypt(LicenseScheme scheme, byte[] publicKey,
//                                   string licenseKey, string fingerprint)
