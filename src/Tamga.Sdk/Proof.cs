namespace Tamga.Sdk;

// STUB — infrastructure scaffold only, no logic yet. See docs/plans/tamga-dotnet.plan.md §H.
// ⚠ security-reviewer review MANDATORY before this file carries real logic.
//
// Intended contents:
//   - Machine offline proof generate/verify: POST
//     /machines/{id}/actions/generate-offline-proof, body
//     { "meta": { "dataset": {...} } }, dataset defaults to {}.
//   - Proof signing is ALWAYS RSA-2048 PKCS#1 v1.5 / SHA-256, regardless of
//     the license's LicenseScheme — do not dispatch by scheme here (unlike
//     Checkout/MachineFile.cs).
//   - Parse response meta.proof = "v1x0.<base64 signature>" — split version
//     prefix from signature, reject malformed/missing-prefix strings.
//   - ⚠ CRITICAL: byte-exact canonical JSON serializer reproducing the
//     server's exact field order for the signed payload:
//     {"account":{"id":...},"machine":{"id":...,"fingerprint":...},
//     "dataset":<client dataset>}. A verifying SDK must reproduce the same
//     serialization, not just the same field set, or the signature check
//     fails. Do NOT rely on System.Text.Json's default property ordering
//     implicitly — write the payload with an explicit Utf8JsonWriter
//     sequence pinned to this exact key order.
//   - Reuse Crypto/Rsa.cs's PKCS1v15/SHA-256 verify path (§F) for proof
//     signature verification.
//   - MachineProof.Verify(RSA publicKey, Guid accountId, Guid machineId,
//     string fingerprint, JsonNode dataset) public API on the parsed type.
//   - This is a lighter-weight alternative to full machine checkout (§F)
//     for periodic "prove this machine is still valid" pings in air-gapped
//     environments.
