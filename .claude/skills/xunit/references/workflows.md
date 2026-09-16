# xUnit Workflows Reference

## Contents
- Adding Tests for a New Feature
- Test-First (TDD) for Domain Aggregates
- Writing an Integration Test
- Debugging Failing Tests
- Coverage Reporting
- Test Configuration Notes

---

## Adding Tests for a New Feature

When you add a command/query handler, you MUST add tests before marking the task complete. Follow this
checklist:

```
Copy this checklist and track progress:
- [ ] Identify target test project: tests/Dragonmind.Knowledge.UnitTests (mocked repository) or
      tests/Dragonmind.Knowledge.IntegrationTests (real database assertions only)
- [ ] Create test class: {Handler}Tests.cs
- [ ] Add happy path [Fact] covering successful execution
- [ ] Add [Fact] for each validation/guard clause (null, empty, an unknown relationship predicate)
- [ ] Add [Fact] verifying repository interactions (Times.Once, Times.Never)
- [ ] Add [Theory] for any check with multiple valid inputs
- [ ] Run tests: dotnet test --filter "FullyQualifiedName~{Handler}Tests"
- [ ] All tests pass — mark task complete
```

**WARNING:** unit tests never touch a database — if a scenario needs one, it belongs in
`tests/Dragonmind.Knowledge.IntegrationTests`, not a mocked repository standing in for one. See the
Anti-Patterns section in `patterns.md`.

---

## Test-First (TDD) for Domain Aggregates

Domain logic should be written test-first. The domain layer has zero infrastructure dependencies,
making this fast.

**Step 1: Write the failing test**
```csharp
[Fact]
public void KnowledgeDocument_WhenEmbeddingSet_RaisesEmbeddingAssignedEvent()
{
    var document = KnowledgeDocument.Create(ScopeId.New(), DocumentContent.Create("..."));

    document.SetEmbedding(Embedding.Create(new float[1536]));

    Assert.Contains(document.DomainEvents, e => e is EmbeddingAssignedDomainEvent);
}
```

**Step 2: Run to confirm it fails**
```bash
dotnet test --filter "FullyQualifiedName~WhenEmbeddingSet_RaisesEmbeddingAssignedEvent"
# → FAIL: KnowledgeDocument does not raise that event yet
```

**Step 3: Implement the minimum to pass, then verify**
```bash
dotnet test --filter "FullyQualifiedName~WhenEmbeddingSet_RaisesEmbeddingAssignedEvent"
# → PASS
```

Repeat: add edge case tests, make them pass, refactor.

---

## Writing an Integration Test

Integration tests hit the real local Postgres + pgvector + Apache AGE container
(`docker compose up -d --wait db`). Workflow:

1. **Join the `"Postgres"` collection** (`[Collection("Postgres")]`) so your class shares the one
   `PostgresFixture` that opened the data source and ran the migration (see `patterns.md`).
2. **Track every primary key you create** in an instance list as you create it.
3. **Delete by that tracked list in `DisposeAsync`**, via `TrackedDocumentCleanup` (or the equivalent for
   the table you're testing) — never a predicate wider than "rows this test created".
4. **If your test conflicts with another class over shared rows**, put both in a collection whose
   `[CollectionDefinition]` sets `DisableParallelization = true`.

```csharp
[Collection("Postgres")]
public class KnowledgeGraphRepositoryIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly List<FactId> _trackedIds = [];

    public KnowledgeGraphRepositoryIntegrationTests(PostgresFixture fixture) => _fixture = fixture;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task AddAsync_ThenGetById_RoundTripsTheFact()
    {
        var repo = new ApacheAgeKnowledgeGraphRepository(_fixture.ContextFactory, NullLogger<ApacheAgeKnowledgeGraphRepository>.Instance);
        var fact = KnowledgeFact.CreateFromStrings(
            ScopeId.New(), "Checkout Service", "Service", "DEPENDS_ON", "Payments DB", "Service");

        await repo.AddAsync(fact);
        _trackedIds.Add(fact.Id);

        var loaded = await repo.GetByIdAsync(fact.Id);
        Assert.NotNull(loaded);
    }

    public async ValueTask DisposeAsync()
    {
        // delete rows named by _trackedIds, strictly by primary key — see patterns.md
    }
}
```

---

## Debugging Failing Tests

**1. Run with verbose output to see failure details:**
```bash
dotnet test --logger "console;verbosity=detailed" \
  --filter "FullyQualifiedName~FailingTestClass"
```

**2. Filter to a single test:**
```bash
dotnet test --filter "FullyQualifiedName=Namespace.ClassName.MethodName"
```

**3. Common failure patterns:**

| Symptom | Likely Cause | Fix |
|---------|-------------|-----|
| `NullReferenceException` in Arrange | Mock not set up for called method | Add `.Setup()` for the missing method |
| `Assert.Equal` wrong order | Arguments reversed | xUnit is `Assert.Equal(expected, actual)` |
| Test passes locally, fails in CI | Static state leaked between tests | Move shared state to constructor, use `IAsyncLifetime` |
| `async` test always passes | Method is `async void` | Change to `async Task` |
| Mock `.Verify` fails with "never called" | Wrong argument matcher | Use `It.IsAny<T>()` or check exact values |
| Integration test fails with connection refused | Local database not running | `docker compose up -d --wait db` |
| Integration test throws on startup instead of skipping | `KNOWLEDGE_TEST_CONNECTION` not set | Set it, or start the database via compose, which sets it for you inside the `tests` service |

**4. Verifying mock calls:**
```csharp
// Verify called exactly once
_mockRepo.Verify(r => r.AddAsync(It.IsAny<KnowledgeFact>(), default), Times.Once);

// Verify called with a specific value
_mockRepo.Verify(r => r.GetByIdAsync(
    It.Is<FactId>(id => id.Value == expectedGuid), default), Times.Once);

// Verify never called
_mockRepo.Verify(r => r.DeleteAsync(It.IsAny<FactId>(), default), Times.Never);
```

---

## Coverage Reporting

Uses coverlet (`coverlet.collector` is referenced in both test projects).

```bash
# Collect coverage for the unit tests
dotnet test tests/Dragonmind.Knowledge.UnitTests --collect:"XPlat Code Coverage"

# Generate an HTML report (requires reportgenerator tool)
dotnet tool install -g dotnet-reportgenerator-globaltool
reportgenerator \
  -reports:"tests/**/coverage.cobertura.xml" \
  -targetdir:"coverage-report" \
  -reporttypes:Html

# Open coverage-report/index.html
```

**Validate: iterate until coverage looks right**
1. Run `dotnet test --collect:"XPlat Code Coverage"`
2. Check output — find uncovered lines in the report
3. Add tests for uncovered branches
4. Repeat from step 1 until satisfied

Note: no runsettings/threshold enforcement exists yet — see "Missing Professional Solutions" in
`patterns.md` for the runsettings snippet to add exclusions and make coverage regressions visible.

---

## Test Configuration Notes

- **No database in unit tests** — every repository dependency is mocked via `Mock<IRepository>`
- **Integration tests** (`tests/Dragonmind.Knowledge.IntegrationTests`) hit the **real** local
  Postgres + pgvector + Apache AGE container via `KNOWLEDGE_TEST_CONNECTION` — never an in-memory
  provider, and a missing connection string is a thrown exception, not a skipped test
- **One migration per run**: `PostgresFixture` is an `ICollectionFixture`, so xUnit constructs exactly
  one instance for the whole `"Postgres"` collection and calls its `InitializeAsync` once — every test
  class in the collection shares that one migrated database and connection pool
- **Destructive cleanup must be scoped by primary key**: resolve the rows a test created to explicit ids
  first, delete only those ids, and throw if the affected row count differs from what was targeted —
  see `TrackedDocumentCleanup` in `patterns.md`. Never widen a teardown predicate
- **Handler registration**: handlers are auto-discovered by MediatR — in tests, instantiate directly
  with `new Handler(mock.Object)`
- **Assertions**: plain `Assert.*` only — there is no FluentAssertions dependency in either test project

See the **dotnet** skill for project file configuration and global usings that affect test compilation.
See the **ddd** skill to understand aggregate patterns being tested.
