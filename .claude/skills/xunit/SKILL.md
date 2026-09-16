---
name: xunit
description: |
  Writes xUnit tests with Fact and Theory attributes following the Arrange-Act-Assert pattern.
  Use when: writing unit tests for the KnowledgeDocument/KnowledgeFact aggregates or their command and query handlers, adding Theory-based parameterized tests, mocking repositories with Moq, writing integration tests against the local Postgres + Apache AGE database, or debugging a failing test in either test project.
allowed-tools: Read, Edit, Write, Glob, Grep, Bash
---

# xUnit Skill

xUnit v3 (3.2.2) is the test framework for both test projects in this solution. Tests follow strict
Arrange-Act-Assert structure and use Moq for dependency mocking. Domain-layer tests require zero
mocking; application/handler tests mock repositories. `tests/Dragonmind.Knowledge.IntegrationTests`
does **not** use an in-memory database — it runs against the real Postgres + pgvector + Apache AGE
container defined in `docker-compose.yml`.

## Quick Start

### Domain Unit Test (no mocks needed)

```csharp
[Fact]
public void Create_WithValidSubjectPredicateObject_SetsProperties()
{
    // Arrange + Act
    var fact = KnowledgeFact.CreateFromStrings(
        ScopeId.New(), "Checkout Service", "Service", "DEPENDS_ON", "Payments DB", "Service");

    // Assert
    Assert.Equal("Checkout Service", fact.Subject.Name);
    Assert.Equal("DEPENDS_ON", fact.Predicate);
    Assert.Equal("Payments DB", fact.Object.Name);
    Assert.True(fact.Id.Value != Guid.Empty);
}
```

### Handler Test (mock repository)

```csharp
[Fact]
public async Task HandleAsync_WithValidCommand_CallsRepositoryAddAsync()
{
    // Arrange
    var mockRepo = new Mock<IKnowledgeGraphRepository>();
    var mockLogger = new Mock<ILogger<CreateKnowledgeFactCommandHandler>>();
    var handler = new CreateKnowledgeFactCommandHandler(mockRepo.Object, mockLogger.Object);
    var command = new CreateKnowledgeFactCommand
    {
        ScopeId = Guid.NewGuid(),
        SubjectName = "Checkout Service",
        SubjectType = "service",
        Predicate = "DEPENDS_ON",
        ObjectName = "Payments DB",
        ObjectType = "service"
    };

    // Act
    await handler.HandleAsync(command);

    // Assert
    mockRepo.Verify(r => r.AddAsync(It.IsAny<KnowledgeFact>(), default), Times.Once);
}
```

### Parameterized Test with Theory

```csharp
[Theory]
[InlineData("DEPENDS_ON", true)]
[InlineData("OWNS", true)]
[InlineData("HALLUCINATED", false)]
public void IsAllowed_ForPredicate_MatchesTheAllowedSet(string predicate, bool expected)
{
    Assert.Equal(expected, RelationshipTypes.IsAllowed(predicate));
}
```

## Key Concepts

| Concept | Usage | Example |
|---------|-------|---------|
| `[Fact]` | Single scenario test | `[Fact] public void X() {}` |
| `[Theory]` + `[InlineData]` | Multiple input sets | `[InlineData("OWNS", true)]` |
| `Mock<T>` | Mock interface dependencies | `new Mock<IKnowledgeGraphRepository>()` |
| `.Setup().ReturnsAsync()` | Mock async methods | `.ReturnsAsync(fact)` |
| `MockBehavior.Strict` | Prove a guard clause short-circuits before a dependency is touched | see below |
| `Assert.Throws<T>` | Verify exceptions | `Assert.Throws<ArgumentException>(() => ...)` |
| `ICollectionFixture<T>` + `IAsyncLifetime` | Integration tests only: one fixture, shared and migrated once, across a whole collection | `PostgresFixture : IAsyncLifetime` |

### `MockBehavior.Strict` as an injection-before-DB tripwire

Rare and intentional — the project default is `Loose`. Use `Strict` with **no setups** to prove a guard
clause runs before any dependency is touched: any call on the strict mock throws, so the test fails if
the guard is bypassed. This is how the Cypher-injection tests on `ApacheAgeKnowledgeGraphRepository`
prove the allowlist check happens before a database connection is ever opened:

```csharp
// SanitizeCypher throws before CreateDbContextAsync is reached, so the mock factory is never
// invoked — calling it would mean the sanitization gate was bypassed.
var mockFactory = new Mock<IDbContextFactory<KnowledgeDbContext>>(MockBehavior.Strict);
var repo = new ApacheAgeKnowledgeGraphRepository(mockFactory.Object, NullLogger<ApacheAgeKnowledgeGraphRepository>.Instance);

await Assert.ThrowsAsync<ArgumentException>(
    () => repo.GetFactsBySubjectAsync("Checkout Service'; DETACH DELETE n RETURN '", ScopeId.New()));
```

Do not use `Strict` as a general "be stricter" mode — that makes tests brittle. Use it only when *no
interaction at all* is the assertion.

## Common Patterns

### Test Naming Convention

**When:** All tests — name must communicate intent without reading the body.

```csharp
// Format: MethodName_Scenario_ExpectedBehavior
public async Task HandleAsync_WithUnknownPredicate_ReturnsNull() { }
public void Create_WithEmptySubject_ThrowsArgumentException() { }
```

### Verifying Domain Events

```csharp
[Fact]
public void KnowledgeDocument_WhenCreated_RaisesKnowledgeDocumentAddedEvent()
{
    var content = DocumentContent.Create("...", source: "architecture-notes", category: "runbook");
    var document = KnowledgeDocument.Create(ScopeId.New(), content);

    Assert.Single(document.DomainEvents);
    Assert.IsType<KnowledgeDocumentAddedDomainEvent>(document.DomainEvents.First());
}
```

See `references/patterns.md` for the full integration-test fixture pattern (migrate-once collection
fixture, cleanup strictly by primary key).

## Running Tests

```bash
dotnet build Dragonmind.Knowledge.sln
dotnet test tests/Dragonmind.Knowledge.UnitTests                  # no DB required

docker compose up -d --wait db                                    # start local Postgres + AGE
dotnet test tests/Dragonmind.Knowledge.IntegrationTests           # KNOWLEDGE_TEST_CONNECTION required

docker compose --profile test up --build --exit-code-from tests   # one-command: build, run everything, exit
```

## See Also

- [patterns](references/patterns.md)
- [workflows](references/workflows.md)

## Related Skills

- See the **csharp** skill for C# language patterns used in test code
- See the **dotnet** skill for `dotnet test` CLI, project configuration, and coverlet setup
- See the **moq** skill for advanced mocking patterns
- See the **ddd** skill for aggregate and domain event patterns being tested
- See the **cqrs** skill for command/query handler test patterns
- See the **coverlet** skill for coverage collection and runsettings configuration
- See the **entity-framework** skill for `KnowledgeDbContext` patterns used by the integration fixture
- See the **postgresql** and **apache-age** skills for what the integration tests assert against
