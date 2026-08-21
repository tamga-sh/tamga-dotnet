# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

`tamga-dotnet` is the official .NET SDK for Tamga (license activation, offline verification, machine management for C#/.NET applications). It is one of the eight hand-written, idiomatic, standalone Tamga SDKs — it reimplements every cryptographic verification primitive natively in C# rather than binding to `tamga-c`'s Rust-backed C ABI, so divergence from the Rust reference implementation in the crypto sections is a real interop bug, not a style choice. The authoritative protocol spec — the source of truth for every field name, endpoint path, and enum value — is the Tamga API protocol specification; read its "Known Server-Side Gaps" section before touching anything.

**Current status: fully implemented and published.** Every section (A–M) is real, tested code — client/transport, license validation/check-in/checkout, machine checkout/management/offline proof, components/processes, entitlements, error model, and CI/release automation. Published on NuGet as `Tamga.Sdk` via Trusted Publishing (OIDC); the exact published version is whatever the newest `v*` git tag says, since MinVer derives it at pack time. Sections E, F, H (`Checkout/LicenseFile.cs`, `Checkout/MachineFile.cs`, `Proof.cs`) have each passed a dedicated `security-reviewer` pass — see "Security-reviewer history" under Critical Dependency Notes below for the specific findings, since they aren't otherwise surfaced anywhere outside commit message bodies.

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
│   │   └── Hkdf.cs                    — BCL HKDF-SHA256; both offline-file key derivations
│   └── Checkout/
│       ├── LicenseFile.cs             — .lic parse/verify/decrypt — Ed25519-only, HKDF AES key, format v2
│       └── MachineFile.cs             — .machine parse/verify/decrypt — multi-algorithm, HKDF key
│
├── tests/Tamga.Sdk.Tests/             — xUnit, mirrors src/ 1:1 (one *Tests.cs per src file)
│
└── .github/workflows/
    ├── ci.yml                         — setup-dotnet + format + test/coverage + codecov, OS matrix
    └── release.yml                    — release-please + MinVer + dotnet pack/push
```

The tree above is abridged: `Models/` also holds `Component.cs`, `Process.cs` and `Entitlement.cs`, `Client.cs` has `Client.*.cs` partial-class siblings, and `samples/` holds five runnable console programs.

**Vertical structure, not horizontal.** `Client.cs` is the single public entry point for every endpoint — do not introduce a `Services/`/`Handlers/` layer. `Models/` holds wire-shape types only; `Crypto/` holds algorithm-only primitives (never derive keys there — key derivation lives in `Hkdf.cs`, the single path for both offline file formats); `Checkout/` composes `Crypto/` + `Models/` into the two offline-file formats.

## Dev Commands

```bash
dotnet restore Tamga.sln              # restore all projects
dotnet build Tamga.sln                # build
dotnet test Tamga.sln                 # run tests (no coverage gate)
dotnet format Tamga.sln               # auto-fix formatting locally
dotnet format Tamga.sln --verify-no-changes   # what CI runs — fails on drift, does not fix
dotnet pack src/Tamga.Sdk/Tamga.Sdk.csproj -c Release -o ./artifacts   # smoke-test packing
```

There is no `just`/`make` wrapper in this repo — these are the actual commands, run them directly. `TreatWarningsAsErrors` is on (`Directory.Build.props`), so a build with any warning fails locally the same way it fails in CI.

## GOTCHAS

Pulled from the Tamga API protocol specification's "Known Server-Side Gaps" section, scoped to what actually affects this repo. Do not "fix" any of these by making the SDK behave as if the gap were closed — model the real, current server behavior.

- **Auth IS enforced, and license-key auth is off by default.** The server accepts an `Authorization: License <key>` credential only when the license's policy has `authentication_strategy` set to `LICENSE` or `MIXED`; that column defaults to `'TOKEN'`, and `NONE` behaves like `TOKEN` at this gate. Anything else answers `401 LICENSE_NOT_ALLOWED` before any endpoint logic runs — a configuration precondition, not a retryable failure. `LICENSE_SUSPENDED` and (under a `REVOKE_ACCESS` policy) `LICENSE_EXPIRED` are refused at the same front door. Do not write docs or samples that imply a license key works against a freshly created policy.
- **16 of 24 `ValidationCode` values are reachable.** `Models/ValidationCode.cs` models all 24 with lenient unknown-value decoding. `ENTITLEMENTS_MISSING` and `FINGERPRINT_SCOPE_MISMATCH` are now genuinely emitted — `scope.entitlements` and `scope.fingerprint` are enforced server-side. Still never emitted: `BANNED`, `TOO_MANY_USERS`, `HEARTBEAT_DEAD`, `HEARTBEAT_NOT_STARTED`, `COMPONENTS_SCOPE_MISMATCH`, and `CHECKSUM_SCOPE_MISMATCH`/`VERSION_SCOPE_MISMATCH` (unreachable for a new reason — sending those two scope fields now fails the whole call with `422 SCOPE_NOT_SUPPORTED`, which is why `Scope.Version`/`Scope.Checksum` are `[Obsolete]` + `[JsonIgnore]`), plus `NOT_FOUND` (the handler returns HTTP 404 directly instead).
- **Every JSON:API error's `status` arrives as a STRING** (`"status": "422"`), because the server serializes it with `status.as_u16().to_string()`. `TamgaJsonOptions.Default` therefore sets `NumberHandling = AllowReadingFromString`, and removing it silently unreachable-ifies the entire typed exception hierarchy: the envelope fails to bind, `TamgaErrorMapper.ToException` is never called, and every API error degrades to a bare `TamgaApiException`. Any new `JsonSerializerOptions` used to read an error body must carry the same flag.
- **Machine/core/memory/disk limits ARE checked at creation time**, through the policy's overage strategy — not only by a later validate. So `POST /machines` can `422` with `MACHINE_LIMIT_EXCEEDED`/`CORE_`/`MEMORY_`/`DISK_LIMIT_EXCEEDED`, and `POST /processes` with `TOO_MANY_PROCESSES`. These map to `TamgaLimitExceededException` subclasses carrying `EquivalentValidationCode`. Because the check honours overage, under `ALLOW_ACCESS`/`ALLOW_1_25X_OVERAGE` the create still succeeds and only validate objects — so `ActivateMachineAsync` must keep BOTH the create-time path and the create→validate→rollback path. Do not delete either.
- **`machine.memory` and `machine.disk` are MEGABYTES**, not bytes, on every model and request builder. A caller passing bytes inflates the license's `machines_memory_count` by 1,048,576× and locks out the next activation.
- **`GET /licenses/{id}/entitlements` ignores `page[after]`** — it unions direct and policy-inherited rows, so the keyset cursor was dropped; `limit` (max 100) is the only bound. `ListEntitlementsAsync` must return `NextCursor = null` unconditionally and `GetCachedEntitlementsAsync` must issue exactly one request. Never reintroduce a cursor loop here. `/machines/{id}/components` is different — its cursor works, and there the cursor is synthesized from the last item's id on a full page, because the server emits no `links` object anywhere (`links: None` on every serializer).
- **Quick-validate silently skips its `last_validated_at` write when the request carries an `Origin` header**, with a byte-identical response either way. `AuthTransport.Cookie` is the only transport this SDK sends `Origin` on, so `QuickValidateAsync` falls back to `POST .../actions/validate` when it is configured.
- **`429 TOO_MANY_REQUESTS` is live and handled.** `Transport.cs` parses and caps `Retry-After`, backs off exponentially with jitter, and auto-retries `GET` plus the seven safe `POST` actions (`validate`, `validate-key`, `check-in`, `check-out`, `ping`, `ping-heartbeat`, `reset-heartbeat`) — creates are deliberately excluded so a retry cannot burn a second seat. `ping-heartbeat`/`reset-heartbeat` must be listed explicitly: neither ends with the `/actions/ping` suffix (that is the *process* route), so leaving them off drops throttled heartbeats and gets machines culled. Retry budget is `TamgaClientOptions.MaxRetries` (default 3). `Errors.cs` deliberately has no typed `429` exception: once the budget is exhausted it surfaces as the catch-all `TamgaApiException`. What does **not** exist is `X-RateLimit-*` response-header parsing — those headers appear in the server's CORS allowlist but are never actually set on a response.
- **`Tamga-Environment` request header is not implemented server-side** (gap #7) — it's a planned EE feature with no code path reading it yet. `Transport.cs` must not send it. This is different from `Tamga-Version`/`Tamga-OTP`, which the server does read.
- **`heartbeat_status` and dead-machine culling ignore `policy.heartbeat_duration`** (gap #8) — both use a hardcoded 600-second window regardless of the policy value. Do not read `policy.heartbeat_duration` client-side and use it to compute a heartbeat interval; `HeartbeatScheduler` (in `Client.cs`) must default to ~1/3 of the hardcoded 600s, not a policy-derived value.
- **Fresh policies return non-real enum strings** (gap #9): `overage_strategy: "DENY_ACCESS"` and `heartbeat_resurrection_strategy: "NO_RESURRECTION"` are not real `OverageStrategy`/`HeartbeatResurrectionStrategy` variants — the server silently treats them as `NO_OVERAGE`/`NO_REVIVE`. `Models/Policy.cs`'s custom `JsonConverter`s for both enums must decode these two strings to `NoOverage`/`NoRevive` without throwing, and must **not** invent a fake `DenyAccess`/`NoResurrection` C# member that implies restrictive behavior the server doesn't actually apply.
- **Auto-update / release-checking is UNIMPLEMENTED here, not broken upstream.** The previous claim in this file — that `GET /releases/actions/upgrade` crashes at runtime and no artifact-download route exists — was wrong, and it was actively blocking work. The upgrade endpoint routes to a live, public handler: `204 No Content` when already current, a `releases` resource otherwise; omitting `constraint` defaults to patch-only (`~x.y.z`) and omitting `channel` matches every channel including `alpha`/`dev`. An artifact-download route exists as well, though it is currently gated behind a permission no role actually holds, so a download method would `403` for every real caller until that is fixed upstream. Adding a `CheckForUpgradeAsync` is a legitimate future feature; adding a download method is not, yet.
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
- No live-network HTTP calls in tests — use a mocked `HttpMessageHandler` (`tests/Tamga.Sdk.Tests/Support/MockHttpMessageHandler.cs`).
- Sections **E, F, H** (`Checkout/LicenseFile.cs`, `Checkout/MachineFile.cs`, `Proof.cs`) additionally require a mandatory `security-reviewer` pass before merge — this is a CRITICAL-severity gate per the org's code-review rule, not optional per reviewer discretion. A subtle bug in these three files (base64-string-vs-decoded-bytes confusion, wrong verifier picked for a scheme, non-deterministic field ordering breaking a signature check, a key derivation that silently isn't the one the server used) is a silent license-verification bypass, not just a functional bug.

## Critical Dependency Notes

**Cryptography backend — `NSec.Cryptography` is the one non-BCL exception.** `System.Security.Cryptography` only gets native Ed25519 support in **.NET 9+**. Since v0.1 targets `net8.0` exclusively, `Crypto/Ed25519.cs` must use **`NSec.Cryptography`** (a libsodium binding) as a NuGet dependency — this is used both for the license-checkout signature verify (always Ed25519, independent of the license's `scheme`) and the Ed25519 branch of the machine-checkout multi-algorithm verify. Every other crypto primitive in this SDK — RSA (PKCS1v15 + PSS), ECDSA P-256, AES-256-GCM, HKDF-SHA256 — uses BCL `System.Security.Cryptography` types that have been available since **.NET Core 3.0**, so those four need no third-party package. **Do not "simplify" by reaching for a BCL Ed25519 type on net8.0** — it does not exist yet and the build will not compile against it. When the SDK eventually raises its floor to `net9.0`, `Crypto/Ed25519.cs` becomes a candidate for migrating off `NSec.Cryptography` to BCL-native — that migration is explicitly out of scope for v0.1.

**`netstandard2.0` is backlog, not v0.1 scope.** Adding it (for broader compatibility — older Unity/Xamarin consumers) would require a second crypto backend: neither BCL-native `AesGcm` nor any Ed25519 primitive exists on `netstandard2.0`, so it would need a BouncyCastle (or similar) fallback for both, maintained and security-reviewed independently of the net8.0 path. Not worth blocking v0.1 on. Do not silently multi-target `Tamga.Sdk.csproj` without adding that fallback first.

**Versioning & Release.** No `<Version>` in `Tamga.Sdk.csproj` to hand-bump — **MinVer** derives the actual NuGet package version from the git tag at `dotnet pack` time. **release-please** (`release-type: simple`) owns changelog generation and tag creation from conventional-commit messages on `main`; it does not touch a version file (there isn't one for it to edit). These two tools have non-overlapping jobs — don't add a manual version bump step "to be safe."

**Publishing method — NuGet.org Trusted Publishing (OIDC), not a stored `NUGET_API_KEY` secret. Live and verified.** `.github/workflows/release.yml`'s `publish` job requests a GitHub OIDC token (`permissions: id-token: write`, plus `contents: read` — a job-level `permissions:` block replaces rather than extends the workflow-level default, so both must be listed explicitly) and exchanges it for a short-lived (~1 hour) NuGet.org API key via the `NuGet/login@v1` action, validated against a trusted-publishing policy registered at nuget.org under the `Tamga` organization (Package Owner: `Tamga`, Repository Owner: `tamga-sh`, Repository: `tamga-dotnet`, Workflow File: `release.yml`, Environment: none). No long-lived secret is stored in this repo. This supersedes an earlier decision to default to a stored `NUGET_API_KEY` secret — that path was dropped once NuGet.org's Trusted Publishing was confirmed to be a documented, supported flow for `dotnet nuget push` specifically (see the official Microsoft Learn "Trusted Publishing" doc), and because NuGet.org started limiting newly-created API keys to 30 days from 2026-08-17 onward anyway, making a stored long-lived key impractical.

**`NuGet/login@v1`'s `user:` input is the trust policy's *creator* account, not the Package Owner.** It must be set to `necipsunmaz` (the nuget.org login that created the policy), never `Tamga` (the org selected as Package Owner inside that policy) — using the org name there fails the OIDC token exchange with HTTP 401 `No matching trust policy owned by user 'Tamga' was found`, confirmed by a real failed publish attempt (`v1.0.2`) before the fix. `v1.0.3` published successfully on the corrected value — see `tamga.sdk`'s NuGet.org listing.

**Trigger structure — publish is gated on `release-please`'s own job output within the same run, not a separate `on: release: types: [published]` trigger.** `release-please-action` creates the GitHub Release using this workflow's own `GITHUB_TOKEN`, and GitHub Actions never lets a `GITHUB_TOKEN`-authored event trigger another workflow run (loop prevention) — a `release: published` trigger here is structurally unreachable, confirmed empirically (the `v1.0.0` release-PR merge produced a Release but no `release`-event run ever fired). Fixed per `release-please-action`'s own documented pattern: `release-please` job exposes `outputs.release_created` from `steps.release.outputs.release_created`, and `publish` job is gated via `needs: release-please` + `if: needs.release-please.outputs.release_created == 'true'`, all within the same `push`-triggered run.

**Private repo checkout requires `.gitattributes`.** `windows-latest` runners default to `core.autocrlf=true`, converting LF-committed files to CRLF on checkout; with no `.gitattributes` to override that, `dotnet format --verify-no-changes` failed its `ENDOFLINE` rule on Windows only even though repo content was all-LF. Fixed with a repo-root `* text=auto eol=lf`.

**Codecov points at coverlet.msbuild's real output path, not a VSTest collector path.** The coverage-gate step uses coverlet.msbuild (`/p:CollectCoverage=true`), which writes `coverage.cobertura.xml` next to the test project (`./tests/Tamga.Sdk.Tests/coverage.cobertura.xml`) — not into a `./coverage/` directory. An earlier `--collect:"XPlat Code Coverage" --results-directory ./coverage` was a second, unused VSTest collector pointing codecov at an effectively empty directory ("Found 0 coverage files to report" on every run); removed, and `ci.yml`'s codecov step now uses `files:` with the real path directly. **Separately unresolved**: `CODECOV_TOKEN` is an org-level secret (`visibility: all`) but has never actually reached this repo's workflow runs (`Token required - not valid tokenless upload`, both on `push` and `pull_request` events) — needs manual verification/re-save in the `tamga-sh` org's GitHub secret settings; not something fixable from this repo.

**Security-reviewer history (back-filled from commit messages — not previously surfaced here).** Sections E/F/H have each undergone an independent `security-reviewer` pass: commit `8fa11a5` — PASS after fixing 1 HIGH finding (`PemEnvelope.Strip` threw an untyped exception on crafted overlapping PEM markers instead of a typed parse error); commit `d13ca72` — PASS on the offline-proof path (Section H) after fixing 1 CRITICAL finding (a JSON-field-ordering deviation from the server's alphabetical `BTreeMap`-backed serialization). Both fixes are live in the current code; this note exists so the review trail is discoverable without archaeology through commit history.

## Branch & Commit Convention

Branches: `feat/*`, `fix/*`, `chore/*`, `refactor/*`, `docs/*`
Commits: [Conventional Commits](https://www.conventionalcommits.org/) (`feat: …`, `fix: …`, etc.) — release-please's changelog/version-bump decision is driven directly by these prefixes on `main`, so an inaccurate type on a squashed PR silently mis-triggers (or skips) a release.
