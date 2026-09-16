# Coverlet Patterns Reference

## Contents
- Configuration approaches
- Exclusion patterns for this codebase
- Threshold enforcement
- Anti-patterns

---

## Configuration Approaches

Coverlet supports two configuration methods. Use inline CLI args for ad-hoc runs; use a `.runsettings` file for consistent CI/local parity.

### Inline CLI (Ad-Hoc)

```bash
dotnet test tests/Dragonmind.Knowledge.UnitTests \
  --collect:"XPlat Code Coverage" \
  -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover \
     DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Exclude="[Dragonmind.Knowledge.Migrations]*,[*]*.GlobalUsings"
```

### .runsettings File (Recommended for CI)

Create `coverlet.runsettings` at the solution root:

```xml
<?xml version="1.0" encoding="utf-8" ?>
<RunSettings>
  <DataCollectionRunSettings>
    <DataCollectors>
      <DataCollector friendlyName="XPlat Code Coverage">
        <Configuration>
          <Format>cobertura</Format>
          <Exclude>[Dragonmind.Knowledge.Migrations]*,[*]*.GlobalUsings</Exclude>
          <ExcludeByAttribute>GeneratedCodeAttribute,CompilerGeneratedAttribute</ExcludeByAttribute>
          <SingleHit>false</SingleHit>
          <IncludeTestAssembly>false</IncludeTestAssembly>
          <Threshold>75</Threshold>
          <ThresholdType>Line</ThresholdType>
          <ThresholdStat>Minimum</ThresholdStat>
        </Configuration>
      </DataCollector>
    </DataCollectors>
  </DataCollectionRunSettings>
</RunSettings>
```

Run with settings file:

```bash
dotnet test tests/Dragonmind.Knowledge.UnitTests \
  --collect:"XPlat Code Coverage" \
  --settings coverlet.runsettings \
  --results-directory ./coverage
```

---

## Exclusion Patterns for This Codebase

This project generates code that must be excluded or coverage metrics are misleading.

### EF Core Migrations

The one migration directory (`Dragonmind.Knowledge/Migrations/`) contains auto-generated code:

```xml
<Exclude>[Dragonmind.Knowledge.Migrations]*</Exclude>
```

### Global Usings

Each project has a `GlobalUsings.cs` — these contain no testable logic:

```xml
<ExcludeByFile>**/GlobalUsings.cs</ExcludeByFile>
```

### The EF Core Design-Time Factory

`KnowledgeDbContextFactory` only runs under `dotnet ef` at design time — it is never exercised by the running application or its tests:

```xml
<ExcludeByFile>**/KnowledgeDbContextFactory.cs</ExcludeByFile>
```

---

## Threshold Enforcement

### WARNING: Threshold Without ThresholdStat Fails Silently

**The Problem:**

```bash
# BAD - missing ThresholdStat, threshold may not apply as expected
-- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Threshold=80
```

**Why This Breaks:**
The default `ThresholdStat` is `Minimum`, which means ANY assembly below 80% fails the build. If a specific area intentionally has lower coverage (e.g., the Apache AGE repository's raw Cypher construction, which the integration tests cover instead), the build fails spuriously.

**The Fix:**

```xml
<!-- For domain unit tests where coverage should be high -->
<Threshold>80</Threshold>
<ThresholdType>Line</ThresholdType>
<ThresholdStat>Minimum</ThresholdStat>
```

Run domain/application tests with a threshold separately from integration tests against a real database:

```bash
# Unit tests: strict threshold, no database required
dotnet test tests/Dragonmind.Knowledge.UnitTests \
  --collect:"XPlat Code Coverage" \
  --settings coverlet.runsettings

# Integration tests: no threshold (exercises real pgvector/AGE, not line coverage)
dotnet test tests/Dragonmind.Knowledge.IntegrationTests \
  --collect:"XPlat Code Coverage"
```

---

## Anti-Patterns

### WARNING: Using coverlet.msbuild Instead of coverlet.collector

**The Problem:**

```xml
<!-- BAD - outdated MSBuild approach -->
<PackageReference Include="coverlet.msbuild" Version="*" />
```

**Why This Breaks:**
1. `coverlet.msbuild` runs at build time, not test time — branch coverage is less accurate
2. It does not support `--collect:"XPlat Code Coverage"` — you need different CLI syntax
3. Parallel test runs cause file locking issues on the coverage output XML

**The Fix:** Use `coverlet.collector` (the VSTest data collector approach):

```xml
<!-- GOOD - in each test .csproj -->
<PackageReference Include="coverlet.collector" Version="8.0.0">
  <PrivateAssets>all</PrivateAssets>
  <IncludeAssets>runtime; build; native; contentfiles; analyzers</IncludeAssets>
</PackageReference>
<PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" />
```

### WARNING: Merging Coverage Reports Incorrectly

When running multiple test projects, each produces a separate `coverage.cobertura.xml`. ReportGenerator merges them — do NOT manually concatenate XML files.

```bash
# GOOD - glob pattern lets ReportGenerator merge
reportgenerator \
  -reports:"./coverage/**/coverage.cobertura.xml" \
  -targetdir:"./coverage/report" \
  -reporttypes:Html

# BAD - only picks up one file
reportgenerator \
  -reports:"./coverage/coverage.cobertura.xml" \
  -targetdir:"./coverage/report"
```
