# Tamga.Sdk

Official .NET SDK for Tamga. Integrate license activation, offline
verification, and machine management into your C#/.NET applications.

> **Status: pre-release scaffold.** This repository currently contains
> infrastructure only (project layout, CI/release automation, stub types
> with doc comments) — no working client yet. The quickstart below shows
> the intended API shape and will not compile until the SDK ships a real
> `0.1.0`. Track progress in
> [`docs/plans/tamga-dotnet.plan.md`](docs/plans/tamga-dotnet.plan.md).

## Install

```bash
dotnet add package Tamga.Sdk
```

Package: [`Tamga.Sdk`](https://www.nuget.org/packages/Tamga.Sdk) on NuGet.
Targets `net8.0` only for v0.1 — see [CLAUDE.md](CLAUDE.md) for why, and
for the `netstandard2.0` backlog note.

## Quickstart (illustrative — SDK is currently a stub)

```csharp
using Tamga.Sdk;

var client = new TamgaClient(new TamgaClientOptions
{
    AccountId = "your-account-id",
    BaseUrl = "https://api.tamga.sh",
});

var result = await client.ValidateByKeyAsync("YOUR-LICENSE-KEY");

if (result.Code == ValidationCode.Valid)
{
    Console.WriteLine("License is valid.");
}
else
{
    Console.WriteLine($"License is not valid: {result.Code}");
}
```

## Documentation

- [`docs/plans/tamga-dotnet.plan.md`](docs/plans/tamga-dotnet.plan.md) — this
  repository's implementation plan (architecture, protocol reference,
  quality gates, CI/release design).
- [`tamga-api/docs/sdk.md`](https://github.com/tamga-sh/tamga-api/blob/main/docs/sdk.md) —
  the authoritative, server-verified protocol/feature spec every SDK in the
  `tamga-sh` organization is built against, including the "Known
  Server-Side Gaps" section describing what not to build against yet.

## License

MIT — see [LICENSE](LICENSE).
