# Moq Patterns Reference

## Contents
- Repository Mocking
- Facade Mocking
- "Bundle" Builder for Multi-Dependency Mocks
- The Caching Facade Decorator Triad
- MockBehavior.Strict as a Tripwire
- Async Patterns
- Argument Matchers
- Verification Patterns
- Anti-Patterns

---

## Repository Mocking

Repositories are domain-layer interfaces. Always mock the interface, never the EF Core implementation.

```csharp
// CORRECT — mock the interface
var mockRepo = new Mock<IKnowledgeDocumentRepository>();

// WRONG — never instantiate EfCoreKnowledgeDocumentRepository directly in tests
// var repo = new EfCoreKnowledgeDocumentRepository(context); ← requires real DB
```

**Typical setup for GetById (nullable return):**

```csharp
var document = KnowledgeDocument.Create(ScopeId.New(), DocumentContent.Create("architecture-notes: Checkout Service"));
mockRepo.Setup(r => r.GetByIdAsync(It.IsAny<DocumentId>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(document);
```

**Setup for AddAsync (returns Task, no value):**

```csharp
mockRepo.Setup(r => r.AddAsync(It.IsAny<KnowledgeDocument>(), It.IsAny<CancellationToken>()))
        .Returns(Task.CompletedTask);
```

---

## Facade Mocking

When testing code that consumes the Knowledge context, mock the ACL facade interface from `Dragonmind.Core/Application/AntiCorruptionLayer/`.

```csharp
var mockKnowledge = new Mock<IKnowledgeContextFacade>();
mockKnowledge.Setup(k => k.SearchKnowledgeAsync("checkout timeouts", null, 5, default))
             .ReturnsAsync(new List<KnowledgeSnippetDto>
             {
                 new()
                 {
                     DocumentId = Guid.NewGuid(),
                     Content = "Checkout Service retries payment calls up to 3 times",
                     RelevanceScore = 0.92,
                     Source = "architecture-notes"
                 }
             });

var lookup = new KnowledgeLookupService(mockKnowledge.Object);
```

**Verify a facade call was made exactly once:**

```csharp
mockKnowledge.Verify(k => k.StoreKnowledgeAsync(
    It.IsAny<string>(),
    It.IsAny<string>(),
    It.IsAny<ScopeId>(),
    default), Times.Once);
```

---

## "Bundle" Builder for Multi-Dependency Mocks

The project's de facto mock-factory convention for a SUT with several collaborators (there is no shared `TestBase` or fixture class). A private `record` bundles the SUT with every mock; a static `Build...()` factory wires them.

```csharp
private record HandlerBundle(
    GetRelatedFactsQueryHandler Handler,
    Mock<IGraphTraversalService> MockGraphTraversalService,
    Mock<ILogger<GetRelatedFactsQueryHandler>> MockLogger);

private static HandlerBundle BuildHandler()
{
    var mockGraphTraversalService = new Mock<IGraphTraversalService>();
    var mockLogger = new Mock<ILogger<GetRelatedFactsQueryHandler>>();
    var handler = new GetRelatedFactsQueryHandler(
        mockGraphTraversalService.Object, mockLogger.Object);
    return new HandlerBundle(handler, mockGraphTraversalService, mockLogger);
}

// Usage in tests:
var bundle = BuildHandler();
bundle.MockGraphTraversalService.Setup(/* ... */);
var result = await bundle.Handler.HandleAsync(/* ... */);
```

Prefer this over repeating 3+ mock declarations per test. Loggers use `NullLogger<T>.Instance` when log output isn't asserted, and a real `Mock<ILogger<T>>` only when a test needs to verify what was logged.

---

## The Caching Facade Decorator Triad

`CachingKnowledgeContextFacade` decorates `IKnowledgeContextFacade` with a scope-aware cache. It is always tested with the same triad of fields: `_mockInner` (the decorated facade interface), `_mockCacheService`, `_mockLogger`, plus `_sut`.

```csharp
// From tests/Dragonmind.Knowledge.UnitTests/Infrastructure/CachingKnowledgeContextFacadeTests.cs
private readonly Mock<IKnowledgeContextFacade> _mockInner;
private readonly Mock<IKnowledgeCacheService> _mockCacheService;
private readonly Mock<ILogger<CachingKnowledgeContextFacade>> _mockLogger;
private readonly CachingKnowledgeContextFacade _sut;
```

Use `.Callback()` with a captured list to assert call order (e.g. the inner facade write must complete before the cache is invalidated):

```csharp
var callOrder = new List<string>();

_mockInner
    .Setup(x => x.AddKnowledgeFactAsync(
        It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ScopeId>(),
        It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
    .Callback(() => callOrder.Add("inner"))
    .ReturnsAsync(true);

_mockCacheService
    .Setup(x => x.InvalidateScopeAsync(It.IsAny<ScopeId>(), It.IsAny<CancellationToken>()))
    .Callback<ScopeId, CancellationToken>((scope, _) => callOrder.Add($"invalidate:{scope.Value}"))
    .Returns(Task.CompletedTask);

await _sut.AddKnowledgeFactAsync(subject, predicate, @object, scopeId);

Assert.Equal("inner", callOrder[0]);
Assert.StartsWith("invalidate:", callOrder[^1]);
```

---

## MockBehavior.Strict as a Tripwire

Rare and intentional — not the project default (default is `Loose`). Use `MockBehavior.Strict` with **no setups** to prove a guard clause short-circuits before a dependency is ever touched: any call on the strict mock throws, so the test fails if the guard is bypassed.

```csharp
// From tests/Dragonmind.Knowledge.UnitTests/Infrastructure/CypherInjectionReadMethodTests.cs
// SanitizeCypher throws before CreateDbContextAsync is reached, so the mock
// factory is never invoked — calling it would indicate the sanitization gate was bypassed.
var mockFactory = new Mock<IDbContextFactory<KnowledgeDbContext>>(MockBehavior.Strict);
var repo = new ApacheAgeKnowledgeGraphRepository(mockFactory.Object, NullLogger<ApacheAgeKnowledgeGraphRepository>.Instance);

await Assert.ThrowsAsync<ArgumentException>(
    () => repo.GetFactsBySubjectAsync("Inventory Service'; DETACH DELETE n RETURN '", ScopeId.New()));
```

Do not use `Strict` as a general "be stricter about setups" mode — that makes tests brittle. Use it only when *no interaction at all* is the assertion.

---

## Async Patterns

All repository and facade methods are async. Use `.ReturnsAsync()` for value-returning methods, `.Returns(Task.CompletedTask)` for void async methods.

```csharp
// Value-returning async
mockRepo.Setup(r => r.GetAllAsync(default)).ReturnsAsync(new List<KnowledgeDocument>());

// void async (Task return)
mockRepo.Setup(r => r.AddAsync(It.IsAny<KnowledgeDocument>(), default)).Returns(Task.CompletedTask);

// Task<bool> return
mockRepo.Setup(r => r.ExistsAsync(It.IsAny<DocumentId>(), default)).ReturnsAsync(true);
```

**NEVER use `.Result` or `.Wait()` in setup callbacks** — this deadlocks in async test contexts. See the **csharp** skill for async rules.

---

## Argument Matchers

| Matcher | Use When |
|---------|----------|
| `It.IsAny<T>()` | Type is correct, value doesn't matter |
| `It.Is<T>(x => x.Prop == val)` | Need to assert specific property |
| `It.IsAny<CancellationToken>()` | Always use for CancellationToken params |
| Exact value | Only when the exact value is semantically important |

```csharp
// Prefer IsAny for CancellationToken — tests shouldn't care about token values
mockRepo.Setup(r => r.GetByIdAsync(documentId, It.IsAny<CancellationToken>()))
        .ReturnsAsync(document);

// Use Is<T> predicate when verifying specific aggregate state
mockRepo.Verify(r => r.AddAsync(
    It.Is<KnowledgeDocument>(d => d.Content.Value.Contains("Checkout Service")),
    default), Times.Once);
```

---

## Verification Patterns

Verify interactions to ensure handlers are actually calling their dependencies — not just returning the right value by coincidence.

```csharp
// Was the repository called exactly once?
mockRepo.Verify(r => r.AddAsync(It.IsAny<KnowledgeDocument>(), default), Times.Once);

// Was DeleteAsync never called? (command should only create, not delete)
mockRepo.Verify(r => r.DeleteAsync(It.IsAny<DocumentId>(), default), Times.Never);

// Was the facade called at least once?
mockKnowledge.Verify(k => k.StoreKnowledgeAsync(
    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ScopeId>(), default),
    Times.AtLeastOnce);
```

**Don't over-verify.** Verify interactions that are meaningful to the behavior under test. Verifying every internal call makes tests brittle and couples them to implementation details.

---

## Anti-Patterns

### WARNING: Mocking Concrete Classes

**The Problem:**
```csharp
// BAD — Moq can only mock virtual members on concrete classes
var mockHandler = new Mock<CreateKnowledgeFactCommandHandler>(mockRepo.Object);
mockHandler.Setup(h => h.HandleAsync(It.IsAny<CreateKnowledgeFactCommand>(), default))
           .ReturnsAsync(new KnowledgeFactDto { /* ... */ }); // Silently doesn't work if not virtual
```

**Why This Breaks:**
1. Moq requires `virtual` methods to override — most handlers use sealed or non-virtual methods
2. You're testing the mock, not the handler — meaningless test
3. Creates hidden coupling: test breaks if implementation details change

**The Fix:**
```csharp
// GOOD — test the real handler with mocked dependencies
var mockRepo = new Mock<IKnowledgeGraphRepository>();
var mockLogger = new Mock<ILogger<CreateKnowledgeFactCommandHandler>>();
var handler = new CreateKnowledgeFactCommandHandler(mockRepo.Object, mockLogger.Object); // real handler
var result = await handler.HandleAsync(command);
```

---

### WARNING: Shared Mock State Between Tests

**The Problem:**
```csharp
// BAD — static or field-level Mock shared across tests
public class HandlerTests
{
    private static Mock<IKnowledgeDocumentRepository> _mockRepo = new(); // shared!

    [Fact]
    public async Task Test1() { /* setups leak into Test2 */ }

    [Fact]
    public async Task Test2() { /* sees Test1's setups */ }
}
```

**Why This Breaks:** xUnit creates a new class instance per test, but `static` mocks persist. Setups from one test contaminate others, causing intermittent failures.

**The Fix:**
```csharp
// GOOD — fresh mock in each test
[Fact]
public async Task Test1()
{
    var mockRepo = new Mock<IKnowledgeDocumentRepository>(); // fresh per test
    // ...
}
```

---

### WARNING: Not Asserting After Verify Failure

`mockRepo.Verify(...)` throws `MockException` if the call didn't happen — but only if you call it. Forgetting `Verify` means the test passes even if the repository was never called.

```csharp
// BAD — no verification; handler could skip the repo call entirely
var result = await handler.HandleAsync(command);
Assert.NotNull(result); // passes even if handler returns garbage without saving

// GOOD — verify the side effect occurred
var result = await handler.HandleAsync(command);
Assert.NotNull(result);
mockRepo.Verify(r => r.AddAsync(It.IsAny<KnowledgeDocument>(), default), Times.Once);
```
