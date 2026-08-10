# Tamga.Sdk

Official .NET SDK for Tamga. Integrate license activation, offline
verification, and machine management into your C#/.NET applications.

> **Status: v0.1 (pre-1.0).** Sections A–K of the implementation plan are
> complete — license validation, check-in, offline license/machine file
> checkout + verification, machine management, components/processes, and
> entitlements are all implemented and covered by the test suite. Track
> progress in
> [`docs/plans/tamga-dotnet.plan.md`](docs/plans/tamga-dotnet.plan.md).

## Install

```bash
dotnet add package Tamga.Sdk
```

Package: [`Tamga.Sdk`](https://www.nuget.org/packages/Tamga.Sdk) on NuGet.
Targets `net8.0` **only** for v0.1 — see [CLAUDE.md](CLAUDE.md) for why
(Ed25519 has no BCL implementation until .NET 9), and for the
`netstandard2.0` backlog note. Do not expect this package to install into a
.NET Framework or older-TFM project yet.

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

var result = await client.ValidateByKeyAsync("YOUR-LICENSE-KEY");

if (result.Code == ValidationCode.Valid)
{
    Console.WriteLine("License is valid.");
}
else
{
    Console.WriteLine($"License is not valid: {result.Code} — {result.Detail}");
}
```

See [`samples/`](samples/) for complete, runnable programs covering
validation, offline license-file checkout/verification, machine
activation + heartbeats, offline proofs, and entitlements.

## Auth transports

`TamgaClientOptions.Auth` accepts one of the following (server tries them
in this order; `License` is the expected default for this SDK's typical
embedded/client use case):

| Transport | Constructor | Header/query sent |
|---|---|---|
| Bearer token | `new AuthTransport.Bearer(token)` | `Authorization: Bearer <token>` |
| Basic (email/password) | `new AuthTransport.BasicEmailPassword(email, password)` | `Authorization: Basic base64(email:password)` |
| Basic (token) | `new AuthTransport.BasicToken(token)` | `Authorization: Basic base64(token:)` |
| Basic (license) | `new AuthTransport.BasicLicense(key)` | `Authorization: Basic base64(license:key)` |
| License key | `new AuthTransport.License(key)` | `Authorization: License <key>` |
| Session cookie | `new AuthTransport.Cookie(sessionId, origin)` | `Cookie: Tamga-Session=<uuid>` + `Origin` header (browser/portal only) |
| Query token | `new AuthTransport.QueryToken(token)` | `?token=<token>` |
| Query auth | `new AuthTransport.QueryAuth(token)` | `?auth=<token>` |

Every issued token is `tok-`-prefixed regardless of documented intent
(`tok-`/`prod-`/`env-`/`activ-`/`lic-`) — this SDK never parses token
prefixes for type detection; treat tokens as opaque strings.

## Known Server-Side Gaps

Condensed from `tamga-api/docs/sdk.md`'s "Known Server-Side Gaps" section,
scoped to what this SDK's consumers need to know:

- **Auth is not enforced** on license or machine endpoints server-side
  today. This SDK still always sends the configured `Auth` transport —
  forward-compatible once enforcement lands.
- Only **14 of 24** `ValidationCode` values are reachable today. `NotFound`
  is declared but never emitted (the server returns HTTP 404 directly
  instead); 9 others (`Banned`, `EntitlementsMissing`, `TooManyUsers`,
  `HeartbeatDead`, `HeartbeatNotStarted`, `FingerprintScopeMismatch`,
  `ComponentsScopeMismatch`, `ChecksumScopeMismatch`,
  `VersionScopeMismatch`) are modeled for forward-compatibility but never
  actually returned — don't build UX around them.
- `429 TOO_MANY_REQUESTS` is declared in the server's error model but never
  actually returned — this SDK deliberately has no client-side
  backoff/retry logic for it.
- Fresh policies can report `overage_strategy: "DENY_ACCESS"` and
  `heartbeat_resurrection_strategy: "NO_RESURRECTION"` — **neither is a
  real enum variant**; the server silently treats them as `NoOverage`/
  `NoRevive`. This SDK's `Policy` decoders map both to the "no
  restriction" variant without throwing.
- Auto-update/release-checking (`GET /releases/actions/upgrade`) is broken
  server-side (missing DB tables) and has no working download-URL endpoint
  even once fixed. This SDK does not implement it.

## Documentation

- [`docs/plans/tamga-dotnet.plan.md`](docs/plans/tamga-dotnet.plan.md) — this
  repository's implementation plan (architecture, protocol reference,
  quality gates, CI/release design).
- [`tamga-api/docs/sdk.md`](https://github.com/tamga-sh/tamga-api/blob/main/docs/sdk.md) —
  the authoritative, server-verified protocol/feature spec every SDK in the
  `tamga-sh` organization is built against, including the full "Known
  Server-Side Gaps" section.
- [`CONTRIBUTING.md`](CONTRIBUTING.md) — local dev setup, build/test
  commands, coding standards.

## License

MIT — see [LICENSE](LICENSE).
