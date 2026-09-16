---
name: dotnet
description: |
  Manages .NET 10.0 runtime, project structure, and solution configuration for this DDD/CQRS Knowledge context.
  Use when: building or running the solution, adding a new aggregate or repository project reference, configuring csproj/global usings/nullable settings, or diagnosing dotnet CLI/build errors.
allowed-tools: Read, Edit, Write, Glob, Grep, Bash
---

# Dotnet Skill

This is a single `.sln` (`Dragonmind.Knowledge.sln`) with two `src/` class libraries and two `tests/` projects, targeting **.NET 10.0** throughout — every `.csproj` has `<Nullable>enable</Nullable>` and `<ImplicitUsings>enable</ImplicitUsings>`, with shared build settings centralized in `Directory.Build.props`.

## Quick Start

### Build the whole solution

```bash
dotnet build Dragonmind.Knowledge.sln
```

### Run just the unit tests

```bash
dotnet test tests/Dragonmind.Knowledge.UnitTests
```

### Run the integration tests (needs a running database)

```bash
docker compose up -d --wait db
dotnet test tests/Dragonmind.Knowledge.IntegrationTests
```

## Key Concepts

| Concept | Usage | Example |
|---------|-------|---------|
| Single project for the context | `Domain/`, `Application/`, `Infrastructure/` are directories, not projects | `Dragonmind.Knowledge/Domain/`, not `Dragonmind.Knowledge.Domain.csproj` |
| Foundation project | Zero infrastructure dependencies; everything else depends on it | `Dragonmind.Core` |
| Global usings | Per-project `GlobalUsings.cs`, not repeated `using` statements | `global using Dragonmind.Core.Domain;` |
| Nullable reference types | Enabled solution-wide, not opt-in per file | `<Nullable>enable</Nullable>` in every `.csproj` |
| Centralized build settings | One `Directory.Build.props` at the repo root | `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>` |
| Design-time DbContext | `dotnet ef` needs a startup project even for a class library | `--startup-project src/Dragonmind.Knowledge` |

## Common Patterns

### Adding a project reference

**When:** A new class in `Dragonmind.Knowledge` needs a type from `Dragonmind.Core` (it almost always already has this reference) or a test project needs to reach both.

```bash
dotnet add src/Dragonmind.Knowledge/Dragonmind.Knowledge.csproj reference src/Dragonmind.Core/Dragonmind.Core.csproj
```

Mirror the `Domain/`, `Application/`, `Infrastructure/` directory layout inside the existing project rather than creating a new `.csproj` per layer — see the **ddd** and **cqrs** skills.

### Verifying a change before calling it done

```bash
dotnet build Dragonmind.Knowledge.sln -warnaserror
dotnet test tests/Dragonmind.Knowledge.UnitTests
```

Never mark work complete on "it compiled" alone — always run the test project for the layer you touched.

## See Also

- [patterns](references/patterns.md)
- [workflows](references/workflows.md)

## Related Skills

- **csharp** — language-level syntax inside the projects this skill builds/runs
- **entity-framework** — `dotnet ef` migrations documented here
- **cqrs** / **ddd** — the Domain/Application/Infrastructure directory convention this skill enforces
- **xunit** / **moq** / **coverlet** — `dotnet test` invocations covered in workflows.md
- **docker** — the containerized Postgres this project connects to at runtime
- **git** — branch/commit conventions for changes made in this solution
