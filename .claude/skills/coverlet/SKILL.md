---
name: coverlet
description: |
  Analyzes code coverage with coverlet instrumentation for this project's xUnit test projects.
  Use when: running coverage collection, configuring coverage thresholds, excluding generated code from metrics, generating coverage reports, or diagnosing why coverage data is missing or incomplete.
allowed-tools: Read, Edit, Write, Glob, Grep, Bash
---

# Coverlet

Code coverage instrumentation for this project's two xUnit test projects. Coverlet integrates via the `XPlat Code Coverage` data collector — no separate install required when `coverlet.collector` is already a package reference.

## Quick Start

### Collect Coverage (Unit Tests)

```bash
dotnet test tests/Dragonmind.Knowledge.UnitTests \
  --collect:"XPlat Code Coverage" \
  --results-directory ./coverage
```

### Collect with Threshold Gate

```bash
dotnet test tests/Dragonmind.Knowledge.UnitTests \
  --collect:"XPlat Code Coverage" \
  -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover \
     DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Threshold=80 \
     DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.ThresholdType=Line
```

### Generate HTML Report (requires `dotnet-reportgenerator-globaltool`)

```bash
dotnet tool install -g dotnet-reportgenerator-globaltool

reportgenerator \
  -reports:"./coverage/**/coverage.cobertura.xml" \
  -targetdir:"./coverage/report" \
  -reporttypes:Html
```

## Key Concepts

| Concept | Usage | Example |
|---------|-------|---------|
| Collector | VSTest data collector | `--collect:"XPlat Code Coverage"` |
| Format | Output format | `cobertura` (default), `opencover`, `lcov` |
| Exclude | Skip types/namespaces | `[Dragonmind.Knowledge.Migrations]*` |
| Threshold | Fail below % | `Threshold=80` |
| Results dir | Where XML lands | `--results-directory ./coverage` |

## Common Patterns

### Exclude EF Core Migrations

Migrations are auto-generated — including them pollutes metrics and creates false negatives.

```xml
<!-- In runsettings or inline -->
<Exclude>[Dragonmind.Knowledge.Migrations]*</Exclude>
```

### Unit vs Integration Coverage

Only the unit test project's coverage is meaningful for a threshold gate — the integration tests exercise the same code paths against a real database and would double-count:

```bash
dotnet test tests/Dragonmind.Knowledge.UnitTests \
  --collect:"XPlat Code Coverage" \
  --results-directory ./coverage/unit
```

## See Also

- [patterns](references/patterns.md)
- [workflows](references/workflows.md)

## Related Skills

- See the **xunit** skill for test structure and Fact/Theory patterns
- See the **dotnet** skill for build and test CLI workflows
- See the **dotnet** skill for global tool installation
