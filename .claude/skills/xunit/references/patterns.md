# xUnit Patterns Reference

## Contents
- Test Organization by Layer
- Domain Tests (No Mocks)
- Handler Tests (Mocked Dependencies)
- Advanced Moq: Capturing State with Callback
- MockBehavior.Strict as a Tripwire
- Theory/InlineData Patterns
- Exception Testing
- Integration Test Fixture Pattern
- Anti-Patterns
- Missing Professional Solutions

---

## Test Organization by Layer

This solution has exactly two test projects — there is no per-context split to navigate:

| What You're Testing | Project |
|---------------------|---------|
| Domain aggregates, value objects, command/query handlers (repository mocked) | `tests/Dragonmind.Knowledge.UnitTests` |
| Real pgvector similarity search and Apache AGE graph behavior against the local compose database | `tests/Dragonmind.Knowledge.IntegrationTests` |

**Rule:** Domain tests never mock anything. Handler tests mock the repository interface. Only the
integration project touches a real database.

---

## Domain Tests (No Mocks)

Domain aggregates are pure C# — instantiate directly, test business logic.

```csharp
public class KnowledgeFactTests
{
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

    [Fact]
    public void Create_WithEmptySubject_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            KnowledgeFact.CreateFromStrings(ScopeId.New(), "", "Service", "DEPENDS_ON", "Payments DB", "Service"));
    }
}
```

---

## Handler Tests (Mocked Dependencies)

Command and query handlers depend on repository interfaces. Always mock the interface, never the EF
Core implementation.

```csharp
public class CreateKnowledgeFactCommandHandlerTests
{
    private readonly Mock<IKnowledgeGraphRepository> _mockRepo;
    private readonly Mock<ILogger<CreateKnowledgeFactCommandHandler>> _mockLogger;
    private readonly CreateKnowledgeFactCommandHandler _handler;

    public CreateKnowledgeFactCommandHandlerTests()
    {
        _mockRepo = new Mock<IKnowledgeGraphRepository>();
        _mockLogger = new Mock<ILogger<CreateKnowledgeFactCommandHandler>>();
        _handler = new CreateKnowledgeFactCommandHandler(_mockRepo.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task HandleAsync_WithAnAllowedPredicate_CallsRepositoryAddAsync()
    {
        var command = new CreateKnowledgeFactCommand
        {
            ScopeId = Guid.NewGuid(),
            SubjectName = "Checkout Service",
            SubjectType = "service",
            Predicate = "DEPENDS_ON",
            ObjectName = "Payments DB",
            ObjectType = "service"
        };

        await _handler.HandleAsync(command);

        _mockRepo.Verify(r => r.AddAsync(It.IsAny<KnowledgeFact>(), default), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WithAnUnknownPredicate_WritesNothingAndReturnsNull()
    {
        var command = new CreateKnowledgeFactCommand
        {
            ScopeId = Guid.NewGuid(),
            SubjectName = "Checkout Service",
            SubjectType = "service",
            Predicate = "HALLUCINATED_RELATIONSHIP",
            ObjectName = "Payments DB",
            ObjectType = "service"
        };

        var result = await _handler.HandleAsync(command);

        Assert.Null(result);
        _mockRepo.Verify(r => r.AddAsync(It.IsAny<KnowledgeFact>(), default), Times.Never);
    }
}
```

---

## Advanced Moq: Capturing State with Callback

**When:** `.Verify(It.Is<T>(...))` isn't enough — you need to assert on a value the handler *computed*
(not one you passed in). Capture the entity handed to the repository, then assert on it directly.

```csharp
[Fact]
public async Task HandleAsync_WithValidCommand_PersistsTheGeneratedEmbedding()
{
    KnowledgeDocument? captured = null;
    _mockRepo
        .Setup(r => r.AddAsync(It.IsAny<KnowledgeDocument>(), It.IsAny<CancellationToken>()))
        .Callback<KnowledgeDocument, CancellationToken>((d, _) => captured = d)
        .Returns(Task.CompletedTask);

    await _handler.HandleAsync(command, CancellationToken.None);

    Assert.NotNull(captured);
    Assert.NotNull(captured!.Embedding);
}
```

See the **moq** skill for more callback and sequence-verification patterns.

---

## MockBehavior.Strict as a Tripwire

Rare and intentional — not the project default (default is `Loose`). Use `MockBehavior.Strict` with **no
setups** to prove a guard clause short-circuits before a dependency is ever touched: any call on the
strict mock throws, so the test fails if the guard is bypassed. This is the pattern behind the
Cypher-injection tests on `ApacheAgeKnowledgeGraphRepository`, whose read methods validate their input
*before* opening a database connection:

```csharp
// SanitizeCypher throws before CreateDbContextAsync is reached, so the mock factory is never
// invoked. Verifiable() is intentionally omitted — calling the factory would mean the
// sanitization gate was bypassed.
var mockFactory = new Mock<IDbContextFactory<KnowledgeDbContext>>(MockBehavior.Strict);
var logger = NullLogger<ApacheAgeKnowledgeGraphRepository>.Instance;
var repo = new ApacheAgeKnowledgeGraphRepository(mockFactory.Object, logger);

var ex = await Assert.ThrowsAsync<ArgumentException>(
    () => repo.GetFactsBySubjectAsync("Checkout Service'; DETACH DELETE n RETURN '", ScopeId.New()));

Assert.Contains("not allowed", ex.Message);
```

Do not use `Strict` as a general "be stricter about setups" mode — that makes tests brittle. Use it only
when *no interaction at all* is the assertion.

---

## Theory/InlineData Patterns

Use `[Theory]` when the same logic applies to multiple inputs. Each `[InlineData]` set is a separate
test run — this is how the relationship allowlist is pinned down without one `[Fact]` per entry.

```csharp
[Theory]
[InlineData("IS_A", true)]
[InlineData("PART_OF", true)]
[InlineData("DEPENDS_ON", true)]
[InlineData("is_a", true)]           // case-insensitive
[InlineData("is a", true)]           // spaces normalize to underscores
[InlineData("HALLUCINATED", false)]
public void IsAllowed_ForPredicate_MatchesTheAllowedSet(string predicate, bool expected)
{
    Assert.Equal(expected, RelationshipTypes.IsAllowed(predicate));
}
```

---

## Exception Testing

```csharp
// Synchronous exception
[Fact]
public void UpdateContent_WithWhitespace_ThrowsArgumentException()
{
    var document = KnowledgeDocument.Create(ScopeId.New(), DocumentContent.Create("valid content"));
    Assert.Throws<ArgumentException>(() => document.SetEmbedding(null!));
}

// Async exception
[Fact]
public async Task HandleAsync_WithNullCommand_ThrowsArgumentNullException()
{
    var handler = new CreateKnowledgeFactCommandHandler(_mockRepo.Object, _mockLogger.Object);
    await Assert.ThrowsAsync<ArgumentNullException>(() => handler.HandleAsync(null!));
}

// Verify exception message
[Fact]
public void Create_WithEmptyContent_ThrowsWithMessage()
{
    var ex = Assert.Throws<ArgumentException>(() => DocumentContent.Create(""));
    Assert.Contains("content", ex.Message, StringComparison.OrdinalIgnoreCase);
}
```

---

## Integration Test Fixture Pattern

Integration tests run against the **real** Postgres + pgvector + Apache AGE container from
`docker-compose.yml` — never an in-memory provider. They require `KNOWLEDGE_TEST_CONNECTION`; a missing
value is a hard failure, not a reason to skip the test.

A single `ICollectionFixture<PostgresFixture>` (one instance for the whole `"Postgres"` collection,
which the collection definition also marks `DisableParallelization = true`) opens the data source,
builds the same DI graph a host would via `AddKnowledgeContext`, and runs the EF Core migration once
before any test in the collection executes:

```csharp
internal sealed class FakeEmbeddingGenerator : IEmbeddingGenerator
{
    // AddKnowledgeContext's DI graph requires an IEmbeddingGenerator; no test in this project
    // actually calls it, so the returned values never matter.
    public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
        => Task.FromResult(new float[EmbeddingDimensions.Default]);
}

public sealed class PostgresFixture : IAsyncLifetime
{
    private ServiceProvider? _serviceProvider;

    public NpgsqlDataSource DataSource { get; private set; } = null!;
    public IDbContextFactory<KnowledgeDbContext> ContextFactory { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("KNOWLEDGE_TEST_CONNECTION")
            ?? throw new InvalidOperationException(
                "KNOWLEDGE_TEST_CONNECTION is not set. Integration tests require the compose database " +
                "(docker compose up -d --wait db) — they never fall back to an in-memory provider.");

        DataSource = KnowledgeDataSource.Create(connectionString);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDistributedMemoryCache();
        services.AddSingleton<IEmbeddingGenerator, FakeEmbeddingGenerator>();
        services.AddKnowledgeContext(DataSource);

        _serviceProvider = services.BuildServiceProvider();
        ContextFactory = _serviceProvider.GetRequiredService<IDbContextFactory<KnowledgeDbContext>>();

        await using var context = await ContextFactory.CreateDbContextAsync();
        await context.Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }

        await DataSource.DisposeAsync();
    }
}

[CollectionDefinition("Postgres", DisableParallelization = true)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
```

**Cleanup is scoped strictly by primary key, tracked by `ScopeId` rather than by document id.** The
database is shared across a whole test run, so teardown resolves the scopes a test minted to
concrete document primary keys *first*, deletes only those, and throws if the delete affects a
different row count than it targeted — a predicate that quietly widens later trips an assertion
instead of deleting someone else's data:

```csharp
[Collection("Postgres")]
public sealed class KnowledgeDocumentRepositoryIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly List<ScopeId> _testScopeIds = new();
    private EfCoreKnowledgeDocumentRepository _repository = null!;
    private IDbContextFactory<KnowledgeDbContext> _contextFactory = null!;

    public KnowledgeDocumentRepositoryIntegrationTests(PostgresFixture fixture) => _fixture = fixture;

    public ValueTask InitializeAsync()
    {
        _contextFactory = _fixture.ContextFactory;
        _repository = new EfCoreKnowledgeDocumentRepository(_contextFactory);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        // Scoped by construction and verified by row count — see TrackedDocumentCleanup.
        await TrackedDocumentCleanup.DeleteTrackedDocumentsAsync(_contextFactory, _testScopeIds);
    }

    private ScopeId CreateTrackedScopeId()
    {
        var scopeId = ScopeId.New();
        _testScopeIds.Add(scopeId);
        return scopeId;
    }

    [Fact]
    public async Task AddAsync_ThenGetById_RoundTripsTheDocument()
    {
        var scopeId = CreateTrackedScopeId();
        var content = DocumentContent.Create("...", source: "architecture-notes", category: "runbook");
        var document = KnowledgeDocument.Create(scopeId, content);

        await _repository.AddAsync(document);
        var loaded = await _repository.GetByIdAsync(document.Id);
        Assert.NotNull(loaded);
    }
}

internal static class TrackedDocumentCleanup
{
    public static async Task DeleteTrackedDocumentsAsync(
        IDbContextFactory<KnowledgeDbContext> contextFactory,
        IReadOnlyCollection<ScopeId> trackedScopeIds,
        CancellationToken cancellationToken = default)
    {
        if (trackedScopeIds.Count == 0)
        {
            return;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Resolve to concrete primary keys before deleting, so the delete below can only ever
        // affect rows belonging to a tracked scope this query already identified.
        var documentIds = await context.Documents
            .Where(d => trackedScopeIds.Contains(d.ScopeId))
            .Select(d => d.Id)
            .ToListAsync(cancellationToken);

        if (documentIds.Count == 0)
        {
            return;
        }

        var deleted = await context.Documents
            .Where(d => documentIds.Contains(d.Id))
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted != documentIds.Count)
        {
            throw new InvalidOperationException(
                $"Cleanup targeted {documentIds.Count} document(s) by primary key but deleted " +
                $"{deleted} — treat this as a cleanup-scoping defect, not a flaky test.");
        }
    }
}
```

If two integration test classes contend over the same rows (for example a vector-search suite reading
what a repository suite just wrote), put both in a collection with `DisableParallelization = true`
rather than relying on xUnit's default parallel test-class execution.

---

## Anti-Patterns

### WARNING: Assert.True for Equality

```csharp
// BAD - failure message says "Expected: True, Actual: False" — useless
Assert.True(result.Subject == "Checkout Service");

// GOOD - failure message says "Expected: Checkout Service, Actual: Payments DB"
Assert.Equal("Checkout Service", result.Subject);
```

### WARNING: Async Void Tests

```csharp
// BAD - exceptions are swallowed, test always passes
[Fact]
public async void HandleAsync_Test() { await _handler.HandleAsync(cmd); }

// GOOD
[Fact]
public async Task HandleAsync_Test() { await _handler.HandleAsync(cmd); }
```

### WARNING: Testing Infrastructure in Domain Tests

```csharp
// BAD - domain tests instantiating a DbContext break isolation and need a live DB
[Fact]
public async Task KnowledgeDocument_SavesCorrectly()
{
    using var ctx = new KnowledgeDbContext(dataSource); // WRONG — belongs in tests/Dragonmind.Knowledge.IntegrationTests
    ...
}

// GOOD - domain tests only touch the aggregate
[Fact]
public void KnowledgeDocument_WhenCreated_HasNoEmbeddingYet()
{
    var document = KnowledgeDocument.Create(ScopeId.New(), DocumentContent.Create("..."));
    Assert.Null(document.Embedding);
}
```

---

## Missing Professional Solutions

### Note: No Coverage Threshold Configuration

`coverlet.collector` is referenced in both test projects, but there is no `coverlet.runsettings` and no
minimum-coverage threshold anywhere. Coverage is collected but nothing fails a build when it regresses.

To add enforcement, create a `coverlet.runsettings` and pass it with `dotnet test --settings coverlet.runsettings`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<RunSettings>
  <DataCollectionRunSettings>
    <DataCollectors>
      <DataCollector friendlyName="XPlat code coverage">
        <Configuration>
          <Format>cobertura</Format>
          <Exclude>[*.Migrations]*,[*]*.Migrations.*</Exclude>
          <ExcludeByAttribute>GeneratedCodeAttribute</ExcludeByAttribute>
        </Configuration>
      </DataCollector>
    </DataCollectors>
  </DataCollectionRunSettings>
</RunSettings>
```

See the **coverlet** skill for threshold enforcement options.

### Note: No `[Trait]` Categorization

No test in the solution uses `[Trait(...)]`; project boundaries (unit vs. integration) are the only
separation. Nice-to-have, not urgent — if you add traits, be consistent (`[Trait("Category", "Integration")]`).

---

See the **moq** skill for advanced setup patterns. See the **csharp** skill for C# record/init property
patterns used in commands.
