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
    // Over-limit activations are rolled back for you — and then THROWN, as
    // MachineOverLimitException (a TamgaLimitExceededException carrying the
    // ValidationResult and the deleted machine id), so you never hold a machine
    // whose row is gone. This branch is for the other invalid codes (Expired,
    // Suspended, …), where the machine is real. Pass deleteOnOverLimit: false
    // to keep the old tuple return instead.
    Console.WriteLine($"Activation rejected: {validation.Code}");
    return;
}

// Size the ping interval from the policy that actually governs the machine.
// The 600s figure DefaultInterval is derived from is only the server's
// fallback; a policy that sets heartbeat_duration to 120 needs a ~40s ping,
// and nothing detects that for you. One round trip at activation time.
TimeSpan interval = await client.GetHeartbeatIntervalAsync(licenseId);

await using var heartbeat = new HeartbeatScheduler(client, machine.Id, interval);
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

### Activation that can run twice

`ActivateMachineAsync` reports a repeat activation of the same fingerprint as
`FingerprintTakenException`, because that is what the server returns. If your
activation step can run more than once — a reinstall, a crash before the machine
id was persisted, a user clicking the button again — use the idempotent form,
which adopts the machine that already holds the fingerprint:

```csharp
MachineActivation activation = await client.ActivateMachineIdempotentAsync(
    new CreateMachineRequest
    {
        Fingerprint = "a-stable-machine-fingerprint",
        LicenseId = licenseId,
    });

if (activation.AlreadyActivated)
{
    Console.WriteLine($"already activated as {activation.Machine.Id}");
}
```

The result also says how the machine was found and what became of it:
`RolledBack` is `true` when this call created the machine, validation came back
over-limit and the row was deleted again — `Machine` is then a tombstone. Since
2.1.2 a same-license conflict is resolved by the id the server names in the
409's `meta.machineId` (one `GET /machines/{id}`, fingerprint re-checked);
without it the license-scoped search runs as before.

Two things it deliberately will not do. It never deletes an adopted machine, even
when validation comes back over-limit — that seat belongs to something this call
did not create. And it never adopts a machine from a *different* license: the
lookup is scoped to `LicenseId`, so under a policy whose
`MachineUniquenessStrategy` is `UNIQUE_PER_POLICY` or `UNIQUE_PER_ACCOUNT` — the
scopes where a conflict can come from another license — the scoped search finds
nothing and the original `FingerprintTakenException` surfaces.

That is the correct outcome, not a gap. Returning another license's machine would
have this client heartbeat and check out a machine its own license does not own
while its own `machines_count` stayed at zero, and since the machine resource
carries no `license_id` it could never detect that. Sharing one fingerprint's
seat across licenses is precisely what the wider uniqueness scopes exist to
prevent. Nothing is lost either: all three strategies' duplicate checks include
the caller's own license rows, so a genuine re-activation conflicts — and is
found — under every one of them. `AlreadyActivated` therefore means "this license
already has this machine", the strong reading.

### Reads

| Call | Route |
|---|---|
| `GetLicenseAsync(id)` | `GET /licenses/{id}` |
| `GetPolicyAsync(id)` | `GET /policies/{id}` — **403s on a license key** |
| `GetLicensePolicyAsync(licenseId)` | `GET /licenses/{id}/policy` — use this one |
| `GetHeartbeatIntervalAsync(licenseId)` | the above, divided by three |
| `GetMachineAsync(id)` | `GET /machines/{id}` |
| `UpdateMachineAsync(id, request)` | `PATCH /machines/{id}` |
| `ListMachinesAsync(...)` | `GET /machines` — **offset**-paginated |
| `FindMachineByFingerprintAsync(licenseId, fp)` | the above, license-scoped and exact-matched client-side |
| `ListMachineProcessesAsync(machineId, ...)` | `GET /machines/{id}/processes` — **keyset**-paginated |
| `DeleteProcessAsync(id)` | `DELETE /processes/{id}` |
| `CheckForUpgradeAsync(request)` | `GET /releases/actions/upgrade` |
| `GetHealthAsync()` | `GET /v1/health` |

`ListMachinesAsync` returns an `OffsetPage<T>`, not the `Page<T>` the entitlement
and component listings use. The two paginate on different mechanisms: the machine
collection sends `meta.page{number,size,total,totalPages}` built from a real
count, so you walk it with `HasMore`; the component listing sends no pagination
metadata at all, so its cursor has to be synthesized from a full page. Reaching
for a cursor on one or a total on the other is the same mistake in opposite
directions, and both directions silently drop rows.

`GetMachineAsync` and the machine listing are also the only network calls in this
SDK whose response is built off a **read**, which is what makes
`HeartbeatStatus.Dead` reachable from them — see [Known gaps](#known-gaps).

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
- **Expiry is enforced, not advisory — on both file formats.**
  `iat`/`exp`/`jti`/`kid` are carried inside the signed bytes
  (`src/Tamga.Sdk/Models/License.cs::LicenseFileClaims`) and checked on every
  verify, with one shared 60-second clock-skew tolerance
  (`src/Tamga.Sdk/Checkout/LicenseFile.cs::LicenseFile.VerifyWithClaims`,
  `src/Tamga.Sdk/Checkout/MachineFile.cs::MachineFile.VerifyWithClaims`). An
  expired-but-authentic file raises `LicenseFileExpiredException`, distinct
  from the `SignatureVerificationException` a forged one raises, so "fetch a
  fresh file" and "someone tampered with this" are not the same outcome. A
  checkout made without a `ttl` legitimately carries no `exp` and never
  expires. Each `VerifyAndDecrypt`/`VerifyWithClaims` has an overload taking
  `nowUnixSeconds`, so an application holding a server-supplied timestamp can
  use it instead of the local clock, which on an offline client is under the
  attacker's control. The `ttl`/`expiry` fields returned in the checkout
  response envelope remain metadata only — they are not signed.
- **A wrong license key is not a forgery.** After a verified signature an
  AES-256-GCM failure can only mean the wrong key material —
  `LicenseKeyMismatchException`, a `SignatureVerificationException` subclass —
  on both file formats and on both the single-key and key-set paths. The
  key-set paths verify the signature against every held key before decoding a
  byte of `enc`; the `kid` only labels a failure
  (`UnknownSigningKeyException` / `UnpublishedSigningKeyException` /
  `SignatureVerificationException`).
- **`alg` is parsed, never sniffed, and format v2 is mandatory.** A machine
  file's `alg` is `<encoding>+<signing-suffix>+v2`; the encoding runs to the
  first `+`, the version marker follows the last `+`, and the suffix is what
  lies between (`MachineFile.VerifyWithClaims`). A file without `+v2` is
  refused — a v1 file carried no `exp` inside its signature and derived its
  AES key without HKDF. `alg` sits outside the signature and is therefore
  attacker-malleable, which is why it is gated rather than trusted.
- **An encrypted machine file's `enc` is two base64 halves, not one blob.**
  It is `<nonce_b64>.<ciphertext_b64>`, decoded independently, with the GCM
  tag already inside the second half — unlike a license file, whose encrypted
  `enc` really is a single `base64(nonce || ciphertext || tag)`. The signature
  is checked over the whole `enc` string before either half is decoded.
- **Verification fails closed.** License files are Ed25519-only
  (`src/Tamga.Sdk/Checkout/LicenseFile.cs::LicenseFile.Verify`). Machine files
  dispatch on the `LicenseScheme` you pass in, never on the file's own
  self-declared `alg`
  (`src/Tamga.Sdk/Checkout/MachineFile.cs::MachineFile.Verify`) — two distinct
  RSA schemes share one `alg` suffix on the wire, so trusting that string
  would be an algorithm-confusion hole. The file's `alg` suffix is a
  cross-check only: a file that contradicts the scheme you passed is refused,
  but it can never widen it. ECDSA verification pins the P-256 curve
  (`src/Tamga.Sdk/Crypto/Ecdsa.cs::Ecdsa.Verify`).
- **Public keys are accepted in the encodings the server actually emits.**
  Ed25519 is a raw 32-byte key; ECDSA P-256 is a raw 65-byte SEC1 uncompressed
  point (`0x04 || X || Y`), not SPKI DER; RSA is accepted as either PKCS#1
  `RSAPublicKey` DER or X.509 `SubjectPublicKeyInfo` DER, because the server
  produces both for the same key depending on which code path you got it from
  (`src/Tamga.Sdk/Crypto/Ecdsa.cs::Ecdsa.TryImportPublicKey`,
  `src/Tamga.Sdk/Crypto/Rsa.cs::Rsa.TryImportPublicKey`).
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
- **19 of the 24 `ValidationCode` values are reachable.** `NotFound` is
  modeled but never emitted (the server returns HTTP 404 directly instead),
  and `Banned`, `ComponentsScopeMismatch`, `ChecksumScopeMismatch` and
  `VersionScopeMismatch` exist for forward-compatibility only. `TooManyUsers`
  (all three validate endpoints, `policy.max_users`), `HeartbeatDead` and
  `HeartbeatNotStarted` (a `Scope.Fingerprint` on a `require_heartbeat`
  policy) **are** reachable as of the API's audit patch, as are
  `EntitlementsMissing` and `FingerprintScopeMismatch` — see the next entry.
- **`Scope`: six fields enforced, two rejected.** `Product`, `Policy`, `User`,
  `Environment`, `Entitlements` and `Fingerprint` all constrain validation.
  `Entitlements` takes entitlement **codes** (not the UUIDs the attach/detach
  bodies use), matched case-insensitively against the union of directly-attached
  and policy-inherited entitlements; `Fingerprint` matches any machine on the
  license regardless of heartbeat status. `Version` and `Checksum` are no longer
  ignored — sending either makes the server fail the entire validate call with
  `422 SCOPE_NOT_SUPPORTED`, so this SDK marks them `[Obsolete]` and never puts
  them on the wire. They are scheduled for removal in the next **major** — not
  the 2.1.0 minor that removed the phantom relationship ids, because their
  obsolete messages carried no removal notice through any shipped release. 2.1.0
  adds that notice; the removal follows it.
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
- **`Policy.HeartbeatDuration` DOES drive the heartbeat window, and
  `HeartbeatScheduler` still does not adapt to it on its own.** The server uses
  `policy.heartbeat_duration` when it is set and falls back to 600 seconds only
  when it is null (`Policy::effective_heartbeat_duration_secs`; the culler
  measures against `COALESCE(p.heartbeat_duration, 600)`). Earlier releases of
  this SDK documented the window as a hardcoded 600s that ignored the policy —
  that was wrong. `DefaultInterval` is still ~1/3 of the 600s **fallback**, and
  on a policy with a shorter `heartbeat_duration` it pings too slowly and the
  machine lapses to `DEAD` between pings. What has changed is that you no longer
  have to find the right number yourself: `GetHeartbeatIntervalAsync(licenseId)`
  reads the governing policy and returns the matching interval, and
  `Policy.EffectiveHeartbeatDurationSeconds` applies the same 600s fallback the
  server does. **Pass an interval to the constructor** — the scheduler takes the
  value once and keeps it, so a policy changed later needs a new scheduler. A
  zero or negative interval falls back to `DefaultInterval` rather than throwing
  (`policy.heartbeat_duration` has no `CHECK` constraint server-side, so a
  hand-rolled window/3 can genuinely produce one); `Timeout.InfiniteTimeSpan`
  still means "never tick", as it always did.
- **You can also obtain the window without a policy read.** A checked-out
  `.machine` file, and now `GetMachineAsync`, both carry a read-backed
  `NextHeartbeatAt`, so `NextHeartbeatAt - LastHeartbeatAt` recovers the
  effective window. Two caveats: `next_heartbeat_at` is
  `last_heartbeat_at + window`, so it is `null` and the window unrecoverable
  until the machine has pinged at least once, and a value read out of a
  `.machine` file is a snapshot from the moment the file was issued, so a later
  policy change is not reflected in a file you already hold.
- **Do NOT derive the window from a ping response.** `CreateMachineAsync`,
  `PingHeartbeatAsync`, `ResetHeartbeatAsync` and `UpdateMachineAsync` return rows
  from statements that do not join `policies`, so their `NextHeartbeatAt` is
  computed against the 600s fallback whatever the policy says. Two responses for
  the same machine seconds apart can disagree, and the endpoint a scheduler
  naturally calls is the one that is wrong. `GetLicensePolicyAsync` and the
  read-backed machines above are the trustworthy sources.
- **"A write-backed response can never say `DEAD`" has one exception:
  `UpdateMachineAsync`.** The rule holds for the ping, the create and the reset
  because each of those writes `last_heartbeat_at` (or nulls it) and the status is
  then derived from the timestamp it just set. `PATCH /machines/{id}` touches no
  heartbeat column, so the status it reports is judged against a timestamp as old
  as it ever was, and `DEAD` is reachable from it. The discriminator is *which
  columns the write touched*, not the HTTP verb.
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
- **No heartbeat-route response can report `HeartbeatStatus.Dead`, and no
  status ever stops the scheduler.** The durable rule, which survives new
  endpoints in a way a route list does not: a response the server builds off a
  **write** it just performed can never say `DEAD`, because the status is
  derived from the timestamp that write set. `ping-heartbeat` sets
  `last_heartbeat_at = NOW()` and so answers `ALIVE` or `RESURRECTED`;
  `CreateMachineAsync` leaves the column unset and `ResetHeartbeatAsync` nulls
  it, both giving `NOT_STARTED`; validation emits `HEARTBEAT_DEAD` only for a
  `Scope.Fingerprint` on a `require_heartbeat` policy, which is a read, not a
  heartbeat route. So an
  `if (status == Dead)` branch written against `HeartbeatScheduler` is
  unreachable code, and re-activation placed inside one never runs. The rule
  covers every status, not just `DEAD`: the loop ends on cancellation, on
  disposal, or on **`404 NOT_FOUND` from the ping** — surfaced on `Faulted` as
  `TamgaNotFoundException`. Hang re-activation off that and nothing else. The
  `Dead` event is kept (a machine-read method would make it live) but no
  heartbeat route can currently raise it.
- **A response built off a *read* can report `DEAD` — and three now reach you.**
  `CheckOutMachineAsync` yields a `.machine` file whose embedded machine is
  resolved server-side through a read query; `MachineFile.VerifyAndDecrypt`
  returns a `Machine` whose `HeartbeatStatus` is bound from that payload. So do
  `GetMachineAsync` and `ListMachinesAsync`, whose query joins `policies`. Even
  there it means only that the last ping is older
  than the window: the cull job early-returns unless `policy.require_heartbeat`
  is set, and that column defaults to `false`, so on a default policy **nothing
  is ever culled** and a machine can sit at `DEAD` indefinitely with its row and
  its seat still there. A later ping revives it.
- **No response carries a `relationships` object.** Every serializer emits
  `{ type, id, attributes }` only, on licenses and machines alike, so nothing on
  a licence or machine read links it to its product, policy, owner or
  environment. Five properties used to claim otherwise — `License.ProductId`/
  `PolicyId`/`UserId`/`EnvironmentId` and `Machine.LicenseId` — and always
  answered `null`. They were `[Obsolete]` from 2.0.0 and **removed in 2.1.0**.
  Track the ids you activated with yourself, or use the dedicated
  `GET /licenses/{id}/product` · `/policy` · `/owner` routes.
  `CreateMachineRequest.LicenseId` is a live request field and is unaffected.
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
  on a failed validation. They are modelled anyway and were deliberately kept
  when the phantom relationship ids were removed in 2.1.0: unlike those, these
  are real wire bindings on a type deserialized straight from `attributes`, so
  they start working with no SDK change the day the server's policy serializer
  projects the two columns it already has. Every one of the other 30 policy
  attributes the serializer emits is modelled; 14 of them were silently missing
  before.
- **`GetPolicyAsync` always `403`s on a license key; `GetLicensePolicyAsync` does
  not.** `GET /policies/{id}` is gated on the `policy.read` permission, which is
  not in the `LicenseToken` role's set — no policy setting turns it on.
  `GET /licenses/{id}/policy` returns the identical resource and is gated on
  `license.read`, which a license key does hold. Embedded clients want that one;
  `GetPolicyAsync` is for admin / developer / product-token / environment-token
  credentials, or when you hold a policy id and no license id.
- **The read routes are not scoped to the caller's own license, and neither are
  the machine writes.** `GET /licenses/{id}`, `GET /policies/{id}` and
  `GET /licenses/{id}/policy` check a permission plus the account on the verified
  credential, but not — unlike validate and check-out — that the id being read is
  the credential's own. A client holding one license key can therefore read every
  license in the account including each one's plaintext `key`. The same omission
  covers machines: `LicenseToken` holds `machine.read`, `machine.update` and
  `machine.delete`, and no machine route applies the per-license scope check, so a
  license key can `PATCH` or `DELETE` any machine in the account. This SDK cannot
  fix any of that; it is reported upstream. Do not expose these routes to an
  untrusted client, and do not build a UI that assumes a license key can only
  reach its own rows.
- **`policy.check_in_interval` is stored in the adverbial form**
  (`daily`/`weekly`/`monthly`/`yearly`), not the noun form this SDK's
  documentation previously claimed. The decoder accepts both, and an unknown
  value falls back to the shortest interval so a policy it cannot read is
  over-served rather than under-served. Read it together with
  `Policy.CheckInIntervalCount` — the period is `count × unit`.
- **There is no exact-match fingerprint filter on the machine collection.** The
  only fingerprint-aware query parameter is `filter[q]`, a case-insensitive
  substring search that also covers `name` and `hostname`.
  `FindMachineByFingerprintAsync` narrows with `filter[license]` plus the
  fingerprint as a search term and then re-checks equality client-side; anything
  that trusted the server's result set directly could return a machine whose
  hostname merely contained the fingerprint.
- **The unfiltered machine listing is account-wide, not license-scoped**, because
  no machine route applies the per-license scope check and `LicenseToken` holds
  `machine.read`. Pass `licenseId` to `ListMachinesAsync` whenever the answer is
  meant to be about one license — the server will not narrow it for you, and the
  machine resource carries no `license_id` to narrow it afterwards. This is why
  `FindMachineByFingerprintAsync` requires a license id rather than offering an
  account-wide convenience overload.
- **Nothing on the server deletes process rows.** The process reaper is not
  wired up, so a process row outlives the process it represents until a client
  removes it — and those rows count against `policy.max_processes`. Call
  `DeleteProcessAsync`, or set `ProcessHeartbeatScheduler.DeleteOnDispose`.
  Machines are different: they do get culled, but only when
  `policy.require_heartbeat` is set, which is not the default.
- **Checkout `includes` is always empty**, and each checkout mints a fresh
  certificate: the call is not idempotent.
- **`x-ratelimit-*` response headers ARE set, and are read back** — this README
  said the opposite until 2.1.0. The rate-limit middleware attaches all four
  (`limit`, `remaining`, `reset`, `window`) to the response it returns, on the
  request it lets through as well as on the `429` it refuses. Read them with
  `TamgaTransport.ReadRateLimitInfo(response)`. Two traps worth knowing:
  `reset` is an **absolute Unix time in seconds**, not a delay, so use
  `ResetAt`; and **absent is not exhausted** — a server with no rate limiter
  configured sets no headers at all, so check `IsPresent` before reading
  `Remaining` as a budget rather than concluding you have none left. This is
  independent of surviving a `429`, which the transport already handles on its
  own.
- **`GetHealthAsync` is a differential diagnostic, not just a ping.**
  `GET /v1/health` is exempt from two gates every other request passes: it is on
  the server's public-route list, so it needs no credential, and it skips the
  `Host`-header check. So if every ordinary call is failing with `403` and *"The
  Host header does not match any configured host"* while this one succeeds, the
  problem is the deployment's allowed-hosts configuration — not your token, not
  your account id, and not anything re-issuing credentials will fix. Note it is a
  liveness probe: the handler never touches the database, so a healthy answer
  does not promise licensing calls will work. Its body is a plain
  `{status, version, uptime_secs}` object, not a JSON:API document.
- **The machine `group` and `owner` sub-resources are not exposed.** Both need
  `groups` and `users` resource models that a licensing client has no use for.
  `/machines/{id}/components` and `/machines/{id}/processes` are both exposed.
- **Component and process REQUEST bodies are flat; their RESPONSES are not.**
  `POST /components` and `POST /processes` take `{machine_id, fingerprint, name,
  metadata}` at the root, unlike the enveloped `POST /machines` — that asymmetry
  is real and deliberate. It is request-only: every response on these routes is
  an ordinary JSON:API `{type, id, attributes}` document. Releases up to and
  including 2.0.x decoded those responses flat, so `CreateComponentAsync`,
  `CreateProcessAsync` and `PingProcessAsync` returned objects with empty ids and
  empty strings, and `ListComponentsAsync` returned the right number of blank
  components. Fixed; if you were working around it by re-fetching, you can stop.

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
