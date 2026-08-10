namespace Tamga.Sdk.Tests.Checkout;

// STUB — mirrors src/Tamga.Sdk/Checkout/MachineFile.cs. No real tests yet.
// See docs/plans/tamga-dotnet.plan.md §F for intended coverage:
//   - verify dispatch picks the correct algorithm per LicenseScheme value
//     (4 positive cases: Ed25519 / RSA-PKCS1 / RSA-PSS / ECDSA-P256)
//   - RSA_2048_JWT_RS256 scheme is rejected client-side with a typed
//     exception, not silently ignored
//   - decrypting with the wrong fingerprint fails closed (wrong HKDF
//     `info` → wrong key → GCM auth failure, not a silent garbage decrypt)
//   - round-trip fixtures for all 4 supported schemes, plain and encrypted
//     variants
