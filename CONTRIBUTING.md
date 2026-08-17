# Contributing to Tamga.Sdk

## Local dev setup

Requires the .NET 8 SDK. No `just`/`make` wrapper — run the `dotnet` CLI
directly.

```bash
dotnet restore Tamga.sln
dotnet build Tamga.sln
dotnet test Tamga.sln
```

## Before opening a PR

Run the same checks CI runs, in this order:

1. **Format check** (fails on drift, does not auto-fix):
   ```bash
   dotnet format Tamga.sln --verify-no-changes
   ```
   If it fails, run `dotnet format Tamga.sln` locally to fix, review the
   diff, then commit the formatting fix as its own step — do not rely on
   CI to auto-fix.

2. **Build** — `TreatWarningsAsErrors` is on (`Directory.Build.props`), so
   any new compiler warning fails the build the same way it fails CI:
   ```bash
   dotnet build Tamga.sln
   ```

3. **Tests + coverage gate** (80% line coverage, `ThresholdType=line`,
   `ThresholdStat=total`):
   ```bash
   dotnet test Tamga.sln -c Release \
     /p:CollectCoverage=true /p:CoverletOutputFormat=cobertura \
     /p:Threshold=80 /p:ThresholdType=line /p:ThresholdStat=total
   ```

4. **CI must pass before merge to `main`.** Branch-protection settings
   enforcing this are a repo-admin action outside this file's scope, but
   the expectation is the same regardless: don't merge on a red CI run.

## Coding standards

- Follow [`ecc:dotnet-patterns`](https://github.com/tamga-sh) conventions:
  `Task`-returning public async methods with a trailing `CancellationToken`
  on every call, DI-friendly `HttpClient` construction (accept an external
  `HttpClient` where the caller wants `IHttpClientFactory` integration),
  nullable-reference-type discipline (`Nullable=enable` is on repo-wide).
- Tests: xUnit, following [`ecc:csharp-testing`](https://github.com/tamga-sh)
  conventions — AAA structure, descriptive behavior-based test names, no
  live network calls (use `tests/Tamga.Sdk.Tests/Support/MockHttpMessageHandler.cs`).
- `tests/Tamga.Sdk.Tests/` mirrors `src/Tamga.Sdk/` 1:1 — add a matching
  `*Tests.cs` file (or extend an existing one) for every source file you
  touch.
- `Client.cs` (and its `TamgaClient` partial-class siblings,
  `Client.*.cs`) is the single public entry point for every endpoint — do
  not introduce a `Services/`/`Handlers/` layer. `Models/` holds wire-shape
  types only. `Crypto/` holds algorithm-only primitives — never derive keys
  there; key derivation lives in `Crypto/Hkdf.cs`, the single derivation
  path for both offline file formats. `Checkout/` composes `Crypto/` +
  `Models/` into the two offline-file formats.

## Security-sensitive sections

Changes to `Crypto/`, `Checkout/`, or `Proof.cs` (sections E, F, H)
require a mandatory `security-reviewer` pass before merge — see `CLAUDE.md`
"Testing". A subtle bug in these files (base64-string-vs-decoded-bytes
confusion, wrong verifier picked for a scheme, non-deterministic field
ordering breaking a signature check, a key derivation that silently isn't
the one the server used) is a silent license-verification bypass, not just
a functional bug — treat findings in these files as CRITICAL severity.

## Ground truth precedence

When this repo's own docs disagree with the running server, in order of
authority:

1. The observed behavior of the running Tamga API server.
2. The Tamga API protocol specification — generated from the server.
3. This repo's own docs and code comments — they can be wrong or
   incomplete about exact field names/signatures.

If you find a discrepancy while implementing something, fix the code to
match ground truth and add an inline note explaining the deviation — don't
silently diverge.

## Commit convention

[Conventional Commits](https://www.conventionalcommits.org/) (`feat: …`,
`fix: …`, `chore: …`, `docs: …`, `test: …`, `refactor: …`, `perf: …`,
`ci: …`). `release-please` reads this history directly to compute the next
version and generate `CHANGELOG.md` — an inaccurate commit type can skip a
release entirely.
