# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

`tamga-dotnet` is the official .NET SDK for Tamga (license activation, offline verification, machine management for C#/.NET applications). It is one of the five hand-written, idiomatic, standalone Tamga SDKs — it reimplements every cryptographic verification primitive natively in C# rather than binding to `tamga-c`'s Rust-backed C ABI, so divergence from the Rust reference implementation in the crypto sections is a real interop bug, not a style choice. Full implementation plan: [`docs/plans/tamga-dotnet.plan.md`](docs/plans/tamga-dotnet.plan.md). Authoritative protocol spec (source of truth for every field name, endpoint path, and enum value — read its "Known Server-Side Gaps" section before touching anything): [`tamga-api/docs/sdk.md`](https://github.com/tamga-sh/tamga-api/blob/main/docs/sdk.md).

**Current status: infrastructure scaffold only.** Every file under `src/Tamga.Sdk/` is a doc-comment stub with no logic — see the plan's Section A for what's scaffolded and Sections B–M for what's still to build.

## Architecture

```
tamga-dotnet/
├── Tamga.sln                          — solution: src + tests
├── Directory.Build.props              — shared Nullable/TreatWarningsAsErrors/LangVersion
├── .editorconfig                      — dotnet format ruleset
├── release-please-config.json         — release-please: release-type "simple"
├── .release-please-manifest.json
│
├── src/Tamga.Sdk/
│   ├── Tamga.Sdk.csproj               — net8.0 ONLY; MinVer + NSec.Cryptography refs
│   ├── Client.cs                      — TamgaClient: public entry point, all endpoint methods
│   ├── Transport.cs                   — HttpClient wrapper, auth header construction
│   ├── Errors.cs                      — TamgaApiError + typed exception hierarchy
│   ├── Proof.cs                       — offline proof generate/verify
│   ├── Models/
│   │   ├── ValidationCode.cs          — 24-value enum, lenient unknown-string decode
│   │   ├── License.cs                 — License resource + Scope + SkipTouch
│   │   ├── Machine.cs                 — Machine resource + HeartbeatStatus
│   │   └── Policy.cs                  — LicenseScheme, OverageStrategy, heartbeat strategies
│   ├── Crypto/
│   │   ├── Ed25519.cs                 — NSec.Cryptography-backed (see gotcha below)
│   │   ├── Rsa.cs                     — BCL RSA (PKCS1v15 + PSS)
│   │   ├── Ecdsa.cs                   — BCL ECDsa (P-256)
│   │   ├── AesGcm.cs                  — BCL AesGcm
│   │   ├── Hkdf.cs                    — BCL HKDF-SHA256
│   │   └── NaiveKey.cs                — license-checkout's non-KDF key derivation
│   └── Checkout/
│       ├── LicenseFile.cs             — .lic parse/verify/decrypt — Ed25519-only, naive AES key
│       └── MachineFile.cs             — .machine parse/verify/decrypt — multi-algorithm, HKDF key
│
├── tests/Tamga.Sdk.Tests/             — xUnit, mirrors src/ 1:1 (one *Tests.cs per src file)
│
└── .github/workflows/
    ├── ci.yml                         — setup-dotnet + format + test/coverage + codecov, OS matrix
    └── release.yml                    — release-please + MinVer + dotnet pack/push
```

Not yet scaffolded (deferred to when Section B–M work starts): `Models/Component.cs`, `Models/Process.cs`, `Models/Entitlement.cs`, `samples/`, `CONTRIBUTING.md`. See the plan for their intended shape.

**Vertical structure, not horizontal.** `Client.cs` is the single public entry point for every endpoint — do not introduce a `Services/`/`Handlers/` layer. `Models/` holds wire-shape types only; `Crypto/` holds algorithm-only primitives (never derive keys there — key derivation lives in `NaiveKey.cs`/`Hkdf.cs`); `Checkout/` composes `Crypto/` + `Models/` into the two offline-file formats.

## Dev Commands

```bash
dotnet restore Tamga.sln              # restore all projects
dotnet build Tamga.sln                # build
dotnet test Tamga.sln                 # run tests (no coverage gate)
dotnet format Tamga.sln               # auto-fix formatting locally
dotnet format Tamga.sln --verify-no-changes   # what CI runs — fails on drift, does not fix
dotnet pack src/Tamga.Sdk/Tamga.Sdk.csproj -c Release -o ./artifacts   # smoke-test packing
```

There is no `just`/`make` wrapper in this repo (unlike `tamga-api`) — these are the actual commands, run them directly. `TreatWarningsAsErrors` is on (`Directory.Build.props`), so a build with any warning fails locally the same way it fails in CI.

## GOTCHAS

Pulled from `tamga-api/docs/sdk.md`'s "Known Server-Side Gaps" section, scoped to what actually affects this repo. Do not "fix" any of these by making the SDK behave as if the gap were closed — model the real, current server behavior.

- **Auth is not enforced on license or machine endpoints server-side today** (gap #3). `Transport.cs` must still always send `Authorization: License <key>` (and the other 4 transports where applicable) — this is forward-compatible for when enforcement lands, not a no-op. Do not skip sending credentials just because the server currently ignores them.
- **Only 14 of 24 `ValidationCode` values are reachable** (gap #4). `Models/ValidationCode.cs` models all 24 with lenient unknown-value decoding, but do not build product UX (retry logic, user-facing messaging, etc.) around the 10 that are declared-but-never-emitted (`BANNED`, `ENTITLEMENTS_MISSING`, `TOO_MANY_USERS`, `HEARTBEAT_DEAD`, `HEARTBEAT_NOT_STARTED`, `FINGERPRINT_SCOPE_MISMATCH`, `COMPONENTS_SCOPE_MISMATCH`, `CHECKSUM_SCOPE_MISMATCH`, `VERSION_SCOPE_MISMATCH`) or `NOT_FOUND` (the handler returns HTTP 404 directly instead — this code never actually appears in a response body).
- **No in-app rate limiting exists** (gap #5). `429 TOO_MANY_REQUESTS` is declared in the server's error enum but has no constructor and is never returned. Do **not** implement client-side 429/backoff handling in `Errors.cs` — there's nothing on the wire to react to, and doing so would be dead code with no test fixture to validate against.
- **`Tamga-Environment` request header is not implemented server-side** (gap #7) — it's a planned EE feature with no code path reading it yet. `Transport.cs` must not send it. This is different from `Tamga-Version`/`Tamga-OTP`, which the server does read.
- **`heartbeat_status` and dead-machine culling ignore `policy.heartbeat_duration`** (gap #8) — both use a hardcoded 600-second window regardless of the policy value. Do not read `policy.heartbeat_duration` client-side and use it to compute a heartbeat interval; `HeartbeatScheduler` (in `Client.cs`, §G of the plan) must default to ~1/3 of the hardcoded 600s, not a policy-derived value.
- **Fresh policies return non-real enum strings** (gap #9): `overage_strategy: "DENY_ACCESS"` and `heartbeat_resurrection_strategy: "NO_RESURRECTION"` are not real `OverageStrategy`/`HeartbeatResurrectionStrategy` variants — the server silently treats them as `NO_OVERAGE`/`NO_REVIVE`. `Models/Policy.cs`'s custom `JsonConverter`s for both enums must decode these two strings to `NoOverage`/`NoRevive` without throwing, and must **not** invent a fake `DenyAccess`/`NoResurrection` C# member that implies restrictive behavior the server doesn't actually apply.
- **Auto-update / release-checking is explicitly out of scope for this SDK** (gaps #1–#2, sdk.md §12). `GET /releases/actions/upgrade` crashes at runtime (missing DB tables/columns) and there's no working artifact-download endpoint even once that's fixed. Do not add a `CheckForUpdateAsync`-style method to `Client.cs` — there's nothing working to call.
- **RFC 9421 HTTP message signing is dead code server-side** (gap #6) — no response is ever signed, `Tamga-Accept-Signature` is parsed but unused. Not currently in this SDK's scope at all; don't add response-signature verification expecting a real signature to show up.

## Testing

- Framework: xUnit. `tests/Tamga.Sdk.Tests/` mirrors `src/Tamga.Sdk/` 1:1 — one `*Tests.cs` per source file, same relative path.
- Coverage gate: **80% line coverage, `ThresholdType=line`, `ThresholdStat=total`**, enforced inline by `coverlet.msbuild` — a coverage drop fails `dotnet test` directly, no separate coverage-check job.
- Local run matching CI's gate:
  ```bash
  dotnet test Tamga.sln -c Release \
    /p:CollectCoverage=true /p:CoverletOutputFormat=cobertura \
    /p:Threshold=80 /p:ThresholdType=line /p:ThresholdStat=total
  ```
- CI matrix is `ubuntu-latest` / `macos-latest` / `windows-latest`. This matters more here than in most repos: `AesGcm` and Ed25519 (via `NSec.Cryptography`/libsodium) are OS-crypto-backend-sensitive (OpenSSL on Linux, CNG on Windows, CommonCrypto on macOS, libsodium's own bundled backend for Ed25519). A crypto test that only runs on the author's laptop is not sufficient signal — every test in `Crypto/`/`Checkout/` must pass identically on all three OSes before merging.
- No live-network HTTP calls in tests — use a mocked `HttpMessageHandler` (`tests/Tamga.Sdk.Tests/Support/MockHttpMessageHandler.cs`, not yet created) once `Transport.cs` has real logic.
- Sections **E, F, H** (`Checkout/LicenseFile.cs`, `Checkout/MachineFile.cs`, `Proof.cs`) additionally require a mandatory `security-reviewer` pass before merge — this is a CRITICAL-severity gate per the org's code-review rule, not optional per reviewer discretion. A subtle bug in these three files (base64-string-vs-decoded-bytes confusion, wrong verifier picked for a scheme, non-deterministic field ordering breaking a signature check, a key derivation that silently isn't the one the server used) is a silent license-verification bypass, not just a functional bug.

## Critical Dependency Notes

**Cryptography backend — `NSec.Cryptography` is the one non-BCL exception.** `System.Security.Cryptography` only gets native Ed25519 support in **.NET 9+**. Since v0.1 targets `net8.0` exclusively, `Crypto/Ed25519.cs` must use **`NSec.Cryptography`** (a libsodium binding) as a NuGet dependency — this is used both for the license-checkout signature verify (always Ed25519, independent of the license's `scheme`) and the Ed25519 branch of the machine-checkout multi-algorithm verify. Every other crypto primitive in this SDK — RSA (PKCS1v15 + PSS), ECDSA P-256, AES-256-GCM, HKDF-SHA256 — uses BCL `System.Security.Cryptography` types that have been available since **.NET Core 3.0**, so those four need no third-party package. **Do not "simplify" by reaching for a BCL Ed25519 type on net8.0** — it does not exist yet and the build will not compile against it. When the SDK eventually raises its floor to `net9.0`, `Crypto/Ed25519.cs` becomes a candidate for migrating off `NSec.Cryptography` to BCL-native — that migration is explicitly out of scope for v0.1.

**`netstandard2.0` is backlog, not v0.1 scope.** Adding it (for broader compatibility — older Unity/Xamarin consumers) would require a second crypto backend: neither BCL-native `AesGcm` nor any Ed25519 primitive exists on `netstandard2.0`, so it would need a BouncyCastle (or similar) fallback for both, maintained and security-reviewed independently of the net8.0 path. Not worth blocking v0.1 on. Do not silently multi-target `Tamga.Sdk.csproj` without adding that fallback first.

**Versioning & Release.** No `<Version>` in `Tamga.Sdk.csproj` to hand-bump — **MinVer** derives the actual NuGet package version from the git tag at `dotnet pack` time. **release-please** (`release-type: simple`) owns changelog generation and tag creation from conventional-commit messages on `main`; it does not touch a version file (there isn't one for it to edit). These two tools have non-overlapping jobs — don't add a manual version bump step "to be safe."

**Publishing method — NuGet.org Trusted Publishing (OIDC), not a stored `NUGET_API_KEY` secret.** `.github/workflows/release.yml`'s `publish` job requests a GitHub OIDC token (`permissions: id-token: write`) and exchanges it for a short-lived (~1 hour) NuGet.org API key via the `NuGet/login@v1` action, validated against a trusted-publishing policy registered at nuget.org under the `Tamga` organization (Package Owner: `Tamga`, Repository Owner: `tamga-sh`, Repository: `tamga-dotnet`, Workflow File: `release.yml`, Environment: none). No long-lived secret is stored in this repo. This supersedes an earlier decision to default to a stored `NUGET_API_KEY` secret — that path was dropped once NuGet.org's Trusted Publishing was confirmed to be a documented, supported flow for `dotnet nuget push` specifically (see the official Microsoft Learn "Trusted Publishing" doc), and because NuGet.org started limiting newly-created API keys to 30 days from 2026-08-17 onward anyway, making a stored long-lived key impractical.

**Known blocker — the trusted-publishing policy needs an org member with nuget.org access to register it.** The policy itself (Package Owner/Repository/Workflow fields above) must be created once, by hand, at nuget.org → account → **Trusted Publishing** — this needs a nuget.org account that's a member of the `Tamga` organization there, which isn't something achievable from this repo or via any CLI. Because `tamga-dotnet` is a **private** repo, the policy also starts in a 7-day "temporarily active" window per publish attempt — if no real publish happens within 7 days of creating (or last restarting) the policy, it goes inactive and needs restarting from the nuget.org UI (no data loss, just re-click). Everything else in the release pipeline (release-please config, MinVer wiring, the `publish` job itself) is in place and was verified locally via `dotnet pack` against a local `v0.1.0-alpha` tag — see `docs/plans/tamga-dotnet.plan.md` §M's checkbox note.

## Branch & Commit Convention

Branches: `feat/*`, `fix/*`, `chore/*`, `refactor/*`, `docs/*`
Commits: [Conventional Commits](https://www.conventionalcommits.org/) (`feat: …`, `fix: …`, etc.) — release-please's changelog/version-bump decision is driven directly by these prefixes on `main`, so an inaccurate type on a squashed PR silently mis-triggers (or skips) a release.
