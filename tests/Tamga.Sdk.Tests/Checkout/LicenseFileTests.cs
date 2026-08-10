namespace Tamga.Sdk.Tests.Checkout;

// STUB — mirrors src/Tamga.Sdk/Checkout/LicenseFile.cs. No real tests yet.
// See docs/plans/tamga-dotnet.plan.md §E for intended coverage:
//   - known-good plain (unencrypted) .lic fixture verifies and parses
//     end-to-end
//   - known-good encrypted .lic fixture verifies, decrypts, and parses;
//     naive key derivation matches server output byte-for-byte
//   - signature verification fails closed on a tampered `sig`
//   - signature verification fails closed on a tampered `enc` payload
//   - ⚠ explicit regression test proving verification is performed against
//     the base64 STRING bytes, not the decoded bytes — construct a fixture
//     where the two byte sources differ and assert only the string-bytes
//     path verifies
//   - unknown/unsupported `alg` value throws a typed error rather than
//     silently no-op-ing
