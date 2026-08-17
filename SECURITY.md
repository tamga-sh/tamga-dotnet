# Security Policy

## Scope

`tamga-dotnet` reimplements every cryptographic verification primitive natively in C# rather than binding to `tamga-c`. The highest-risk code lives in:

- [`src/Tamga.Sdk/Crypto/`](src/Tamga.Sdk/Crypto/) — Ed25519 (NSec.Cryptography), RSA-PKCS1/PSS, ECDSA-P256, AES-256-GCM, and the HKDF-SHA256 key derivation used by both offline file formats.
- [`src/Tamga.Sdk/Checkout/`](src/Tamga.Sdk/Checkout/) — `.lic`/`.machine` file parse/verify/decrypt.
- [`src/Tamga.Sdk/Proof.cs`](src/Tamga.Sdk/Proof.cs) — offline proof generate/verify.

## Supported Versions

The latest published minor version receives security fixes. From 1.x
onwards, the two most recent minor versions receive security patches.

## Reporting a Vulnerability

**Do not open a public GitHub issue for a suspected security vulnerability.**

Report it privately via GitHub's [private vulnerability reporting](https://github.com/tamga-sh/tamga-dotnet/security/advisories/new)
feature on this repository. Include:

- The affected file(s)/function(s) and, if possible, a minimal reproduction.
- Whether the issue is a verification bypass (a forged `.lic`/`.machine` file
  or offline proof that this SDK would incorrectly accept as valid), an
  information leak, a denial-of-service via malformed/adversarial input, or
  something else.
- The version (git commit or tagged release) you tested against.

You should receive an initial response within 5 business days. Confirmed
vulnerabilities will be fixed in a private branch and disclosed via a GitHub
Security Advisory alongside the patched release; we will credit reporters
who wish to be credited.

## What Counts as a Vulnerability Here

Given this SDK's actual attack surface (an offline file/proof verifier, not
a server), the highest-severity class of bug is **a verifier that accepts
something it should reject** — for example, a signature check computed over
the wrong bytes, a scheme dispatch that picks the wrong algorithm, or an
offline proof that verifies against a differently-serialized (but
semantically equivalent) payload.

## What the Current Implementation Guarantees

- **Both offline file formats derive their AES-256-GCM key with
  HKDF-SHA256 (RFC 5869).** A `.lic` file uses
  `salt = "tamga:license-file-key-v1"`, `ikm = <license key>`,
  `info = "license-file"`
  (`src/Tamga.Sdk/Crypto/Hkdf.cs::Hkdf.DeriveLicenseFileKey`). A `.machine`
  file uses `salt = "tamga:machine-file-key-v1"`, `ikm = <license key>`,
  `info = <machine fingerprint>`
  (`src/Tamga.Sdk/Crypto/Hkdf.cs::Hkdf.DeriveMachineFileKey`), so a machine
  file cannot be decrypted anywhere but on the machine it was issued for.
  The pre-v2 license-file transform (the license key's raw UTF-8 bytes
  zero-padded to 32) has been **removed, not deprecated** — there is no code
  path left that can produce or consume it.
- **Offline license files must be format v2.**
  `src/Tamga.Sdk/Checkout/LicenseFile.cs::LicenseFile.VerifyWithClaims`
  rejects any certificate whose `alg` does not end in `+v2`, and rejects a
  payload with no signed `meta` object, before any payload is handed back.
- **`exp` is enforced client-side, not opt-in.** The `iat`/`exp`/`jti`/`kid`
  claims live inside the signed bytes
  (`src/Tamga.Sdk/Models/License.cs::LicenseFileClaims`) and
  `LicenseFile.VerifyWithClaims` throws
  `LicenseFileExpiredException` once `exp` has passed, allowing a fixed
  60-second clock-skew tolerance
  (`LicenseFile.ClockSkewToleranceSeconds`). An overload takes the current
  time as a parameter so an application holding a server-supplied timestamp
  can avoid trusting a user-controlled system clock.
- **Signature verification fails closed.** `.lic` files are Ed25519-only
  (`src/Tamga.Sdk/Checkout/LicenseFile.cs::LicenseFile.Verify`); `.machine`
  files dispatch on the caller-supplied `LicenseScheme`, never on the file's
  own self-declared `alg` string
  (`src/Tamga.Sdk/Checkout/MachineFile.cs::MachineFile.Verify`), which is
  what keeps `RSA_2048_PKCS1_SIGN` and `RSA_2048_JWT_RS256` — both of which
  serialize to the same `alg` suffix — from being confused for one another.
  ECDSA verification additionally pins the P-256 curve
  (`src/Tamga.Sdk/Crypto/Ecdsa.cs::Ecdsa.Verify`).
- **HTTP 429 is handled client-side.**
  `src/Tamga.Sdk/Transport.cs::TamgaTransport.SendWithRetryAsync` retries a
  rate-limited request with jittered exponential backoff
  (`TamgaTransport.RetryDelay`), preferring a parsed and capped `Retry-After`
  (`TamgaTransport.ParseRetryAfter`, capped at 60 seconds). Auto-retry is
  scoped to `GET` plus five safe `POST` actions — `validate`, `validate-key`,
  `check-in`, `check-out`, `ping` — by
  `TamgaTransport.IsRetryable`; creates are deliberately excluded so a
  retried activation cannot burn a second seat.

## Compatibility Break: v1 Offline License Files

Offline license files issued in the pre-v2 format are **rejected outright**,
with no fallback path. A caller holding a v1 `.lic` file must re-check out
the license against a current server. This is a real behavioral break, not a
deprecation — see the "Offline verification" section of the README.

## Known, Deliberate Non-Vulnerabilities

The following are intentional design decisions, not bugs, and reports about
them will be closed without action (though corrections/clarifications are
welcome):

- Auth is not currently enforced server-side on the license/machine
  validate/check-in endpoints (a server-side gap, not a client-side one) —
  this SDK still always sends its configured credentials for
  forward-compatibility
  (`src/Tamga.Sdk/Transport.cs::TamgaTransport.ApplyAuth`).
- `429 TOO_MANY_REQUESTS` has no dedicated typed exception. Once the retry
  budget in `TamgaClientOptions.MaxRetries` is exhausted, it surfaces as the
  catch-all `TamgaApiException` carrying the parsed error
  (`src/Tamga.Sdk/Errors.cs::TamgaErrorMapper.ToException`).
- The SDK does not parse `X-RateLimit-*` response headers. They are declared
  in the server's CORS allowlist but never actually set on a response, so
  there is nothing on the wire to read.
