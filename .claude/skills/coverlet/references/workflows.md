# Coverlet Workflows Reference

## Contents
- Local development workflow
- CI coverage gate workflow
- Coverage by Test Project
- Iterate-until-pass pattern

---

## Local Development Workflow

Copy this checklist for a full local coverage run:

```
- [ ] Run unit tests with coverage collection
- [ ] Verify XML files generated in ./coverage/
- [ ] Generate HTML report
- [ ] Open report and identify uncovered paths
- [ ] Write tests or update exclusions
- [ ] Re-run and confirm metric improvement
```

### Step 1: Run Unit Tests with Coverage

```bash
dotnet test tests/Dragonmind.Knowledge.UnitTests \
  --collect:"XPlat Code Coverage" \
  --results-directory ./coverage \
  --settings coverlet.runsettings
```

### Step 2: Verify Output Exists

```bash
find ./coverage -name "coverage.cobertura.xml" | wc -l
# Should be at least 1
```

### Step 3: Generate and Open HTML Report

```bash
reportgenerator \
  -reports:"./coverage/**/coverage.cobertura.xml" \
  -targetdir:"./coverage/report" \
  -reporttypes:Html \
  -assemblyfilters:"+Dragonmind.*;-Dragonmind.*.UnitTests;-Dragonmind.*.IntegrationTests"

# Windows
start ./coverage/report/index.html

# macOS/Linux
open ./coverage/report/index.html
```

---

## CI Coverage Gate Workflow

Fail the pipeline if unit-test coverage drops below threshold. Unit tests have no external dependencies — 80%+ coverage is achievable and expected. Integration tests hit real PostgreSQL/pgvector/AGE code and are collected but not gated, since they measure the same lines the unit suite already gates plus paths only a real database can exercise.

```bash
# Unit tests: strict 80% line coverage gate
dotnet test tests/Dragonmind.Knowledge.UnitTests \
  --collect:"XPlat Code Coverage" \
  --settings coverlet.runsettings \
  --results-directory ./coverage/unit

# Integration tests: collect but no gate
dotnet test tests/Dragonmind.Knowledge.IntegrationTests \
  --collect:"XPlat Code Coverage" \
  --results-directory ./coverage/integration
```

`coverlet.runsettings`:

```xml
<?xml version="1.0" encoding="utf-8" ?>
<RunSettings>
  <DataCollectionRunSettings>
    <DataCollectors>
      <DataCollector friendlyName="XPlat Code Coverage">
        <Configuration>
          <Format>cobertura</Format>
          <Exclude>[Dragonmind.Knowledge.Migrations]*</Exclude>
          <ExcludeByFile>**/GlobalUsings.cs,**/KnowledgeDbContextFactory.cs</ExcludeByFile>
          <Threshold>80</Threshold>
          <ThresholdType>Line</ThresholdType>
          <ThresholdStat>Minimum</ThresholdStat>
        </Configuration>
      </DataCollector>
    </DataCollectors>
  </DataCollectionRunSettings>
</RunSettings>
```

---

## Coverage by Test Project

This solution has two test projects, and they measure different things — don't merge their reports into one gate.

### Unit Tests (no database)

```bash
dotnet test tests/Dragonmind.Knowledge.UnitTests \
  --collect:"XPlat Code Coverage" \
  --results-directory ./coverage/unit

reportgenerator \
  -reports:"./coverage/unit/**/coverage.cobertura.xml" \
  -targetdir:"./coverage/unit/report" \
  -reporttypes:Html \
  -assemblyfilters:"+Dragonmind.Knowledge;+Dragonmind.Core"
```

### Both Projects, Merged for a Combined View

```bash
# dotnet test parallelizes multiple projects automatically
dotnet test Dragonmind.Knowledge.sln \
  --collect:"XPlat Code Coverage" \
  --results-directory ./coverage \
  --settings coverlet.runsettings \
  --logger "console;verbosity=minimal"

# Merge both into one report
reportgenerator \
  -reports:"./coverage/**/coverage.cobertura.xml" \
  -targetdir:"./coverage/merged" \
  -reporttypes:"Html;Badges;TextSummary" \
  -assemblyfilters:"+Dragonmind.*;-*.UnitTests;-*.IntegrationTests"
```

---

## Iterate-Until-Pass Pattern

When the coverage gate fails, use this loop:

```
1. Run: dotnet test tests/Dragonmind.Knowledge.UnitTests --collect:"XPlat Code Coverage" --settings coverlet.runsettings
2. If the build fails with a threshold error:
   a. Generate report: reportgenerator -reports:"./coverage/**/coverage.cobertura.xml" -targetdir:"./coverage/report" -reporttypes:Html
   b. Open the report and find uncovered lines in domain aggregates/value objects
   c. Add missing tests in tests/Dragonmind.Knowledge.UnitTests
   d. See the **xunit** skill for test patterns
3. Repeat step 1 until the gate passes
4. Commit both test files and any updated runsettings
```

### WARNING: Do Not Lower Thresholds to Pass the Gate

**The Problem:**

```xml
<!-- BAD - you decreased the threshold because tests were hard to write -->
<Threshold>60</Threshold>
```

**Why This Breaks:**
Thresholds exist to catch regressions. Lowering them permanently hides coverage debt. Domain aggregates and value objects have zero external dependencies — they are the easiest code in the codebase to test. If coverage is below 80% on domain classes, tests are missing, not optional.

**The Fix:** Write the missing tests. Domain objects like `KnowledgeDocument`, `KnowledgeFact`, `DocumentContent`, and `Embedding` require no mocks. See the **xunit** skill for Fact/Theory patterns for value object edge cases.
