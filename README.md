# Tamga.Sdk

[![CI](https://github.com/tamga-sh/tamga-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/tamga-sh/tamga-dotnet/actions/workflows/ci.yml)
[![NuGet version](https://img.shields.io/nuget/v/Tamga.Sdk.svg)](https://www.nuget.org/packages/Tamga.Sdk)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Official .NET SDK for Tamga. Integrate license activation, offline
verification, and machine management into your .NET applications.

## Install

```bash
dotnet add package Tamga.Sdk
```

Targets `net8.0` only. Ed25519 has no BCL implementation before .NET 9, so
the package takes a single non-BCL dependency, `NSec.Cryptography`
(`src/Tamga.Sdk/Crypto/Ed25519.cs`). There is no `netstandard2.0` target, so
the package will not install into a .NET Framework project.

## Quickstart

```csharp
using Tamga.Sdk;
using Tamga.Sdk.Models;

using var client = new TamgaClient(new TamgaClientOptions
{
    AccountId = "your-account-id",
    BaseUrl = "https://api.tamga.sh",
    Auth = new AuthTransport.License("YOUR-LICENSE-KEY"),
});

ValidationResult result = await client.ValidateByKeyAsync("YOUR-LICENSE-KEY");

if (result.Code == ValidationCode.Valid)
{
    Console.WriteLine($"Valid. Uses: {result.License.Uses}.");
}
else
{
    Console.WriteLine($"Not valid: {result.Code} — {result.Detail}");
}
```

Activating a machine against a license, then keeping it alive:

```csharp
using Tamga.Sdk;
using Tamga.Sdk.Models;

var (machine, validation) = await client.ActivateMachineAsync(new CreateMachineRequest
{
    Fingerprint = "a-stable-machine-fingerprint",
    LicenseId = licenseId,
    Hostname = Environment.MachineName,
});

if (!validation.Valid)
{
    // Over-limit activations are rolled back for you: ActivateMachineAsync
    // deletes the machine it just created when validation comes back
    // TooManyMachines / TooManyCores / TooMuchMemory / TooMuchDisk /
    // TooManyProcesses, unless you pass deleteOnOverLimit: false.
    Console.WriteLine($"Activation rejected: {validation.Code}");
    return;
}

await using var heartbeat = new HeartbeatScheduler(client, machine.Id);
heartbeat.Pinged += m => Console.WriteLine($"heartbeat ok: {m.HeartbeatStatus}");
// No `heartbeat.Dead` handler on purpose: a ping response can never say DEAD
// (it writes last_heartbeat_at = NOW(), then reports on that), so such a
// handler is dead code. No heartbeat status stops the loop — only a 404 does.
heartbeat.Faulted += ex =>
{
    // A 404 from the ping is the one signal that the machine really is gone.
    if (ex is TamgaNotFoundException)
    {
        Console.WriteLine("machine deleted server-side — re-activate");
        return;
    }

    Console.WriteLine($"ping failed: {ex.Message}");
};
heartbeat.Start();
```

[`samples/`](samples/) holds five runnable console programs covering
validation, offline license checkout and verification, machine activation
with heartbeats, offline proofs, and entitlements.

## Auth transports

`TamgaClientOptions.Auth` accepts any one of the eight transports below
(`src/Tamga.Sdk/Transport.cs::AuthTransport`, applied by
`TamgaTransport.ApplyAuth`). `License` is the expected default for this
SDK's typical embedded/client use case; `null` sends no credentials at all.

| Transport | Constructor | Sent as |
|---|---|---|
| Bearer token | `new AuthTransport.Bearer(token)` | `Authorization: Bearer <token>` |
| Basic (email/password) | `new AuthTransport.BasicEmailPassword(email, password)` | `Authorization: Basic base64(email:password)` |
| Basic (token) | `new AuthTransport.BasicToken(token)` | `Authorization: Basic base64(token:)` |
| Basic (license) | `new AuthTransport.BasicLicense(key)` | `Authorization: Basic base64(license:key)` |
| License key | `new AuthTransport.License(key)` | `Authorization: License <key>` |
| Session cookie | `new AuthTransport.Cookie(sessionId, origin)` | `Cookie: Tamga-Session=<uuid>` + `Origin` (browser/portal only) |
| Query token | `new AuthTransport.QueryToken(token)` | `?token=<token>` |
| Query auth | `new AuthTransport.QueryAuth(token)` | `?auth=<token>` |

Tokens are opaque strings. Every issued token carries a `tok-` prefix
regardless of its documented intent (`tok-`/`prod-`/`env-`/`activ-`/`lic-`),
so this SDK never parses a prefix to infer a token's type.

A TOTP code can be attached to every authenticated request with
`TamgaClientOptions.Otp`, which is sent as the `Tamga-OTP` header.

## Offline verification

> **Compatibility warning — v1 offline license files are rejected.** A
> `.lic` file must be format v2: its `alg` has to end in `+v2` and its
> payload has to carry the signed `meta` claims. Pre-v2 files are rejected
> outright with no fallback path
> (`src/Tamga.Sdk/Checkout/LicenseFile.cs::LicenseFile.VerifyWithClaims`),
> so a caller holding a v1-issued file must check the license out again
> against a current server.

Check out a license file, then verify and decrypt it with no network access:

```csharp
using Tamga.Sdk;
using Tamga.Sdk.Checkout;
using Tamga.Sdk.Models;

LicenseFile file = await client.CheckOutLicenseAsync(licenseId, encrypt: true, ttl: 3600);

// Persist file.Certificate somewhere; verify it later, offline.
byte[] accountPublicKey = Convert.FromBase64String(accountEd25519PublicKeyBase64);

try
{
    License license = file.VerifyAndDecrypt(accountPublicKey, "YOUR-LICENSE-KEY");
    Console.WriteLine($"Verified license {license.Id}, uses {license.Uses}.");
}
catch (LicenseFileExpiredException ex)
{
    Console.WriteLine($"File expired at {ex.ExpiresAt} — check out a fresh one.");
}
catch (SignatureVerificationException)
{
    Console.WriteLine("File failed verification — treat as untrusted.");
}
```

`VerifyWithClaims` returns the signed claims alongside the license, for
`jti` replay detection or `kid` key-rotation bookkeeping, and lets you supply
the current time rather than trusting a user-controlled system clock:

```csharp
(License license, LicenseFileClaims claims) = file.VerifyWithClaims(
    accountPublicKey,
    "YOUR-LICENSE-KEY",
    serverSuppliedUnixSeconds);

Console.WriteLine($"jti={claims.Id} kid={claims.KeyId} exp={claims.ExpiresAt}");
```

Machine files work the same way, except that they are bound to one machine
and that the signature scheme comes from the license's own policy rather than
being fixed:

```csharp
using Tamga.Sdk.Checkout;
using Tamga.Sdk.Models;

MachineFile machineFile = await client.CheckOutMachineAsync(machineId, encrypt: true, ttl: 3600);

Machine machine = machineFile.VerifyAndDecrypt(
    LicenseScheme.Ed25519Sign,
    accountPublicKey,
    "YOUR-LICENSE-KEY",
    "a-stable-machine-fingerprint");
```

`ttl` is validated client-side before the request is sent, mirroring the
server's `> 0 && <= 31536000` range check
(`src/Tamga.Sdk/Checkout/MachineFile.cs::MachineFile.ValidateTtl`).

## Security notes

- **Both offline file formats derive their AES-256-GCM key with
  HKDF-SHA256.** A license file uses
  `salt = "tamga:license-file-key-v1"`, `ikm = <license key>`,
  `info = "license-file"`
  (`src/Tamga.Sdk/Crypto/Hkdf.cs::Hkdf.DeriveLicenseFileKey`); a machine file
  uses `salt = "tamga:machine-file-key-v1"`, `ikm = <license key>`,
  `info = <machine fingerprint>`
  (`src/Tamga.Sdk/Crypto/Hkdf.cs::Hkdf.DeriveMachineFileKey`), which is why a
  machine file cannot be decrypted anywhere but on the machine it was issued
  for. The pre-v2 license-file transform — the license key's raw UTF-8 bytes
  zero-padded to 32 — was removed rather than deprecated; no code path can
  produce or consume it.
- **Expiry is enforced, not advisory.** `iat`/`exp`/`jti`/`kid` are carried
  inside the signed bytes (`src/Tamga.Sdk/Models/License.cs::LicenseFileClaims`)
  and checked on every verify, with a fixed 60-second clock-skew tolerance
  (`src/Tamga.Sdk/Checkout/LicenseFile.cs::LicenseFile.VerifyWithClaims`).
  The `ttl`/`expiry` fields returned in the checkout response envelope remain
  metadata only — they are not signed.
- **Verification fails closed.** License files are Ed25519-only
  (`src/Tamga.Sdk/Checkout/LicenseFile.cs::LicenseFile.Verify`). Machine files
  dispatch on the `LicenseScheme` you pass in, never on the file's own
  self-declared `alg`
  (`src/Tamga.Sdk/Checkout/MachineFile.cs::MachineFile.Verify`) — two distinct
  RSA schemes share one `alg` suffix on the wire, so trusting that string
  would be an algorithm-confusion hole. ECDSA verification pins the P-256
  curve (`src/Tamga.Sdk/Crypto/Ecdsa.cs::Ecdsa.Verify`).
- **Signatures cover the base64 string, not the decoded bytes.** Both file
  formats sign the UTF-8 bytes of the `enc` base64 string itself
  (`LicenseFile.Verify`, `MachineFile.Verify`). Any reimplementation that
  hashes the decoded payload will reject every genuine file.
- **Offline proofs are always RSA-2048 PKCS#1 v1.5 / SHA-256**, over a
  recursively alphabetically key-sorted canonical JSON payload
  (`src/Tamga.Sdk/Proof.cs::MachineProof.BuildSignedPayload`,
  `MachineProof.Verify`) — the ordering the server's own serializer produces.
- **HTTP 429 is retried with backoff.**
  `src/Tamga.Sdk/Transport.cs::TamgaTransport.SendWithRetryAsync` retries a
  rate-limited request using jittered exponential backoff
  (`TamgaTransport.RetryDelay`), preferring a parsed `Retry-After` capped at
  60 seconds (`TamgaTransport.ParseRetryAfter`). Auto-retry is scoped to
  `GET` plus five safe `POST` actions — `validate`, `validate-key`,
  `check-in`, `check-out`, `ping`, `ping-heartbeat`, `reset-heartbeat`
  (`TamgaTransport.IsRetryable`). Creates are deliberately excluded, because a
  repeated `POST /machines` can burn a second seat. Set
  `TamgaClientOptions.MaxRetries` to `0` to handle `429` yourself.

Vulnerability reporting and the full threat model live in
[SECURITY.md](SECURITY.md).

## Known gaps

Behaviors of the current server that a consumer of this SDK needs to plan
around:

- **License-key auth is off by default.** The server accepts an
  `Authorization: License <key>` credential only when the license's policy has
  `authentication_strategy` set to `LICENSE` or `MIXED` — and that column
  defaults to `TOKEN`. Against a default policy every call answers
  `401 LICENSE_NOT_ALLOWED` (`LicenseNotAllowedException`). That is a
  configuration precondition, not a transient failure: retrying or re-issuing
  the key will not help, the policy has to be changed. Suspended licenses
  (`401 LICENSE_SUSPENDED`) and expired licenses under a `REVOKE_ACCESS` policy
  (`401 LICENSE_EXPIRED`) are refused at the same front door, before any
  per-endpoint check runs.
- **16 of the 24 `ValidationCode` values are reachable.** `NotFound` is
  modeled but never emitted (the server returns HTTP 404 directly instead),
  and `Banned`, `TooManyUsers`, `HeartbeatDead`, `HeartbeatNotStarted`,
  `ComponentsScopeMismatch`, `ChecksumScopeMismatch` and `VersionScopeMismatch`
  exist for forward-compatibility only. Do not build UX around those.
  `EntitlementsMissing` and `FingerprintScopeMismatch` **are** reachable now —
  see the next entry.
- **`Scope`: six fields enforced, two rejected.** `Product`, `Policy`, `User`,
  `Environment`, `Entitlements` and `Fingerprint` all constrain validation.
  `Entitlements` takes entitlement **codes** (not the UUIDs the attach/detach
  bodies use), matched case-insensitively against the union of directly-attached
  and policy-inherited entitlements; `Fingerprint` matches any machine on the
  license regardless of heartbeat status. `Version` and `Checksum` are no longer
  ignored — sending either makes the server fail the entire validate call with
  `422 SCOPE_NOT_SUPPORTED`, so this SDK marks them `[Obsolete]` and never puts
  them on the wire.
- **Machine `Memory` and `Disk` are MEGABYTES, not bytes.** The server stores
  and quota-checks these columns in megabytes. Reporting 16 GB as
  `17179869184` instead of `16384` inflates the license's running total by a
  factor of 1,048,576 and trips `MEMORY_LIMIT_EXCEEDED` on the next activation
  against that license.
- **Activation limits are enforced at creation time too**, not only by a later
  validation. `POST /machines` can fail with `422 MACHINE_LIMIT_EXCEEDED` /
  `CORE_` / `MEMORY_` / `DISK_LIMIT_EXCEEDED`, surfaced as
  `TamgaLimitExceededException` subclasses whose `EquivalentValidationCode`
  gives the matching `ValidationCode`. The create-time check runs through the
  policy's overage strategy, so under `ALLOW_ACCESS`/`ALLOW_1_25X_OVERAGE` the
  create still succeeds and the limit surfaces only at validate — which is why
  `ActivateMachineAsync` keeps its create→validate→rollback path as well.
- **`GET /licenses/{id}/entitlements` cannot be paginated.** It returns a union
  of direct and policy-inherited rows, so the server ignores `page[after]` on
  this route; `limit` (max 100) is the only bound. `Page.NextCursor` is always
  `null` here, and a license with more than 100 effective entitlements cannot be
  enumerated in full — so a `false` from `HasEntitlementAsync` is authoritative
  only below that ceiling. Component listings are unaffected: their cursor
  genuinely works.
- **Quick-validate skips its `last_validated_at` write when the request carries
  an `Origin` header**, and the response is byte-identical either way.
  `AuthTransport.Cookie` is the one transport this SDK sends `Origin` on, so
  `QuickValidateAsync` transparently uses `POST .../actions/validate` instead
  when it is configured. A proxy that adds `Origin` to another transport
  defeats that; use `ValidateByIdAsync` if that is a risk.
- **A fresh policy can report enum strings that are not real variants**
  (`overage_strategy: "DENY_ACCESS"`, `heartbeat_resurrection_strategy:
  "NO_RESURRECTION"`). The server treats both as the no-restriction case, and
  so do this SDK's decoders — neither is surfaced as a distinct C# member,
  because that would imply a restriction the server does not apply.
- **`Policy.HeartbeatDuration` DOES drive the heartbeat window — and this SDK
  cannot read it.** The server uses `policy.heartbeat_duration` when it is set
  and falls back to 600 seconds only when it is null
  (`Policy::effective_heartbeat_duration_secs`; the culler measures against
  `COALESCE(p.heartbeat_duration, 600)`). Earlier releases of this SDK
  documented the window as a hardcoded 600s that ignored the policy — that was
  wrong. But the correction cuts both ways: there is no policy getter here, so
  `HeartbeatScheduler` cannot discover the effective window and its
  `DefaultInterval` is ~1/3 of the 600s **fallback**. On a policy with a
  shorter `heartbeat_duration`, that default pings too slowly and the machine
  lapses to `DEAD` between pings — **pass your own `interval`**. The SDK cannot
  detect this for you.
- **Whether `Machine.NextHeartbeatAt` reflects the real window depends on the
  route.** The value is derived from a window carried on the row, populated
  only when the loading query joined `policies`. `CreateMachineAsync`,
  `PingHeartbeatAsync` and `ResetHeartbeatAsync` return `INSERT`/`UPDATE …
  RETURNING` rows with no join, so their `NextHeartbeatAt` (and
  `HeartbeatStatus`) use the 600s fallback. The machine inside a `.machine`
  file from `CheckOutMachineAsync` **is** resolved through a joining query, so
  it carries the policy-derived value — reading
  `NextHeartbeatAt - LastHeartbeatAt` off a checked-out machine is the one way
  this SDK can observe the effective window.
- **`HeartbeatStatus.Dead` is not observable from any call this SDK makes, and
  the scheduler must never be stopped by a status.** `ping-heartbeat` writes
  `last_heartbeat_at = NOW()` and the server derives `heartbeat_status` from
  that same timestamp, so a ping answers `ALIVE` or `RESURRECTED` — never
  `DEAD`. `CreateMachineAsync` and `ResetHeartbeatAsync` both yield
  `NOT_STARTED`, and validation never emits `HEARTBEAT_DEAD`. A
  `if (status == Dead)` branch written against `HeartbeatScheduler` is
  unreachable code, and re-activation placed inside one never runs. The rule is
  about every status, not just `DEAD`: the loop ends on cancellation, on
  disposal, or on **`404 NOT_FOUND` from the ping** — surfaced on `Faulted` as
  `TamgaNotFoundException`. Hang re-activation off that and nothing else. The
  `Dead` event is kept (a machine-read method would make it live) but cannot
  currently fire.
- **Where `DEAD` *is* visible, it does not mean the machine was culled.** The
  one route that can surface it today is the machine inside a `.machine` file
  from `CheckOutMachineAsync`, which is resolved through a read query. Even
  there it means only that the last ping is older than the window: the cull job
  early-returns unless `policy.require_heartbeat` is set, and that column
  defaults to `false`, so on a default policy **nothing is ever culled** and a
  machine can sit at `DEAD` indefinitely with its row and its seat still there.
  A later ping revives it.
- **No response carries a `relationships` object.** Every serializer emits
  `{ type, id, attributes }` only, on licenses and machines alike, so
  `License.ProductId`/`PolicyId`/`UserId`/`EnvironmentId` and `Machine.LicenseId`
  can never be populated from a read. All five are `[Obsolete]` and always
  `null`; track the ids you activated with yourself, or use the dedicated
  `GET /licenses/{id}/product` · `/policy` · `/owner` routes.
- **`ResetHeartbeatAsync` and `GenerateOfflineProofAsync` always `403` on a
  license key.** Both are role-gated (admin / developer / product token /
  environment token, plus sales/support agents for proofs) rather than
  permission-gated, and the license-key role is not on either list — even though
  it holds `machine.proofs.generate`. `PingHeartbeatAsync` is permission-gated
  and works fine. This matters most for reset: it is the only server-side way to
  unstick a wedged heartbeat job, so an embedded client cannot self-recover.
- **`Policy.MaxMemory` and `Policy.MaxDisk` are absent from `GET` responses**
  even though both are enforced during validation, so they cannot be
  introspected client-side — only observed as `TooMuchMemory`/`TooMuchDisk`
  on a failed validation.
- **Checkout `includes` is always empty**, and each checkout mints a fresh
  certificate: the call is not idempotent.
- **`X-RateLimit-*` response headers are not parsed**, because the server
  never actually sets them.
- **Auto-update and release-checking are not implemented here** — but the
  server-side endpoint does work, contrary to what earlier versions of this
  section claimed. `GET /v1/accounts/{id}/releases/actions/upgrade` is a live,
  public handler: it answers `204 No Content` when the caller is already
  current, and a `releases` resource otherwise. Omitting `constraint` defaults
  to patch-only (`~x.y.z`); omitting `channel` matches **every** channel,
  including `alpha` and `dev`. An artifact-download route exists too, though it
  is currently behind a permission that no role holds. None of this is exposed
  by this SDK yet — it is a missing feature, not a blocked one.

## Documentation

- [tamga.sh](https://tamga.sh) — product documentation and the API reference.
- [`samples/`](samples/) — five runnable end-to-end console programs.
- [CONTRIBUTING.md](CONTRIBUTING.md) — local dev setup, build/test commands,
  coding standards.
- [SECURITY.md](SECURITY.md) — threat model and vulnerability reporting.
- XML doc comments ship in the package, so IntelliSense carries the
  per-endpoint notes inline.

## License

MIT — see [LICENSE](LICENSE).
