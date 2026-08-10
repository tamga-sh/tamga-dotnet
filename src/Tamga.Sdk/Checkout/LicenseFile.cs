namespace Tamga.Sdk.Checkout;

// STUB — infrastructure scaffold only, no logic yet. See docs/plans/tamga-dotnet.plan.md §E.
// ⚠ security-reviewer review MANDATORY before this file carries real logic.
//
// Intended contents:
//   PEM wrapper parse/serialize: -----BEGIN LICENSE FILE-----
//   / -----END LICENSE FILE-----, wrapping base64 JSON {enc, sig, alg}.
//
//   `alg` is exactly one of:
//     "base64+ed25519"          (plain)
//     "aes-256-gcm+ed25519"     (encrypted)
//   Ed25519 ONLY for the checkout signature — independent of the license's
//   own LicenseScheme (contrast with Checkout/MachineFile.cs §F, which
//   dispatches by scheme).
//
//   Full verify pipeline (one method):
//     strip PEM markers → base64-decode → parse {enc, sig, alg} JSON →
//     base64-decode `sig` → Ed25519-verify against `enc`'s STRING bytes →
//     base64-decode `enc` → if `alg` contains "aes-256-gcm", split
//     nonce(12B) ‖ ciphertext ‖ tag(16B) and AES-256-GCM-open with the
//     Crypto/NaiveKey.cs-derived key → parse resulting bytes as the
//     {"data": ...} JSON payload.
//
//   ⚠ CRITICAL — the single most consequential correctness trap in this
//   SDK: Ed25519-verify `sig` against `enc`'s ASCII/UTF-8 bytes of the
//   BASE64 STRING ITSELF, NOT the base64-decoded bytes. Get the byte
//   source wrong and every `.lic` file either fails verification (safe but
//   broken) or, worse, a bug that skips verification silently accepts
//   forged files. Flag with an inline `// CRITICAL:` comment at the call
//   site when this gains a real implementation.
//
//   GOTCHA: `includes` is always [] server-side — do not build an
//   "embedded relationships via checkout" feature around it.
//   GOTCHA: checkout `id` is a fresh UUIDv7 per call, not idempotent.
//   GOTCHA: `ttl`/`expiry` are metadata-only, NOT embedded in the signed
//   payload and NOT re-checked server-side on later validation — expiry
//   enforcement for an offline file is entirely this SDK's responsibility.
//   GOTCHA: server returns 422 LICENSE_NOT_ENCRYPTED when the license has
//   no `key` set and encrypt=true is requested.
//
//   Public API:
//     LicenseFile.Verify(ReadOnlySpan<byte> publicKey)
//     LicenseFile.VerifyAndDecrypt(ReadOnlySpan<byte> publicKey, string licenseKey)
