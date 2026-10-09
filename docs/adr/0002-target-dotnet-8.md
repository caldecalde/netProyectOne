# 0002 – Target .NET 8 (LTS) with a planned move to .NET 10

**Status:** Accepted

## Context
The target environment standardizes on .NET 8 / C# 12. .NET 8 support ends in November 2026, and .NET 10 (LTS) is the successor.

## Decision
- All projects target `net8.0` with `LangVersion 12`. These are set once in `Directory.Build.props` and the SDK is pinned in `global.json`.
- Package versions are centrally managed in `Directory.Packages.props`. Packages that track the ASP.NET Core shared framework (JwtBearer, Mvc.Testing, Serilog.AspNetCore, Swashbuckle) stay on their 8.x lines to avoid mixing framework versions.
- Analyzers run at `latest-recommended` with warnings treated as errors.

## Upgrade path to .NET 10
1. Bump `global.json` and `TargetFramework`, then move the framework-tracking packages to 10.x.
2. Replace Swashbuckle with the built-in `Microsoft.AspNetCore.OpenApi` document generation.
3. Fix any new analyzer findings (for example CA1515) and re-run the full test and e2e suite.

Because of the central configuration this touches about three files, plus whatever the analyzers flag.
