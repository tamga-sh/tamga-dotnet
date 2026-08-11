## Summary

<!-- What does this PR do, and why? -->

## Checklist

- [ ] `dotnet format Tamga.sln --verify-no-changes` passes
- [ ] `dotnet build Tamga.sln -c Release` passes
- [ ] `dotnet test Tamga.sln -c Release` passes with the 80% line coverage gate
- [ ] Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/)
- [ ] If this touches `/src/Tamga.Sdk/Crypto/`, `/src/Tamga.Sdk/Checkout/`, `/src/Tamga.Sdk/Proof.cs`: a `security-reviewer` pass was requested and CRITICAL/HIGH findings addressed

## Test plan

<!-- How did you verify this works? -->
