# Contributing

PbiTwin is a privacy tool, so a few rules are not negotiable. A change that breaks one of them will not be merged.

- **No network code.** No `System.Net`, `HttpClient`, sockets, telemetry or update checks. `check-no-network.sh` and a unit test enforce it.
- **Only approved packages.** `Microsoft.AnalysisServices` and `Microsoft.AnalysisServices.AdomdClient` (plus xUnit and its runners in tests). `DependencyAllowlistTests` fails on anything else. Packages are pinned and `packages.lock.json` is committed.
- **Original values never reach the disk.** Only the twin is written. Reports and warnings contain counts and column names, never values, and the seed is never saved.
- **The input project is never modified.**
- **A twin that leaks must fail verification.** If you add a place where text can end up in the output, the leak scan has to see it.
- **Keep it simple.** If a feature needs a lot of code, open an issue first.

## Building and testing

```
dotnet restore --locked-mode
dotnet build
dotnet run --project samples/generator   # recreates the sample data
dotnet test
```

The tests use seeded random inputs (`tests/PbiTwin.Core.Tests/Fuzz`), so a failure always reproduces. Please add a test with every fix.

## Commits

Short English messages in the imperative ("Escape M text literals"). Describe what changed and why.
