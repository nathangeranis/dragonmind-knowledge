# Moq Workflows Reference

## Contents
- Testing a New Command Handler
- Testing a New Query Handler
- Testing a Facade Consumer
- Testing Error Paths
- Simulating Transient Failure Then Recovery
- Special Case: Mock<IMediator> via Scope Factory
- Running and Filtering Tests
- Checklist: Adding Handler Tests

---

## Testing a New Command Handler

Command handlers take a command record, mutate state via a repository, and return a result. Tests must verify both the return value and that the repository interaction occurred.

```csharp
public class AddKnowledgeDocumentCommandHandlerTests
{
    [Fact]
    public async Task HandleAsync_ValidCommand_StoresDocumentAndReturnsDto()
    {
        // Arrange
        var mockRepo = new Mock<IKnowledgeDocumentRepository>();
        var mockEmbedding = new Mock<IEmbeddingService>();

        mockEmbedding.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), default))
                     .ReturnsAsync(Embedding.Create(new float[EmbeddingDimensions.Default]));
        mockRepo.Setup(r => r.AddAsync(It.IsAny<KnowledgeDocument>(), default))
                .Returns(Task.CompletedTask);

        var handler = new AddKnowledgeDocumentCommandHandler(mockRepo.Object, mockEmbedding.Object);
        var command = new AddKnowledgeDocumentCommand
        {
            Content = "runbook: rotate the Payments DB credentials quarterly",
            ScopeId = ScopeId.New().Value
        };

        // Act
        var result = await handler.HandleAsync(command);

        // Assert
        Assert.NotEqual(Guid.Empty, result.DocumentId);
        mockRepo.Verify(r => r.AddAsync(
            It.Is<KnowledgeDocument>(d => d.Content.Value == command.Content),
            default), Times.Once);
        mockEmbedding.Verify(e => e.GenerateEmbeddingAsync(command.Content, default), Times.Once);
    }
}
```

**Workflow:**
1. Identify all constructor dependencies of the handler
2. Create a `Mock<T>` for each interface dependency
3. Setup the happy-path behavior (what the dependencies return)
4. Construct the handler with `.Object` instances
5. Execute `HandleAsync`
6. Assert the return value
7. `Verify` each repository/service call that must have occurred

---

## Testing a New Query Handler

Query handlers read from repositories and map to DTOs. Test both the found and not-found paths.

```csharp
public class GetKnowledgeDocumentQueryHandlerTests
{
    [Fact]
    public async Task HandleAsync_ExistingDocument_ReturnsDto()
    {
        // Arrange
        var documentId = DocumentId.New();
        var document = KnowledgeDocument.Create(ScopeId.New(), DocumentContent.Create("ADR-7: prefer the outbox pattern"));
        var mockRepo = new Mock<IKnowledgeDocumentRepository>();
        mockRepo.Setup(r => r.GetByIdAsync(It.IsAny<DocumentId>(), default))
                .ReturnsAsync(document);

        var handler = new GetKnowledgeDocumentQueryHandler(mockRepo.Object);

        // Act
        var result = await handler.HandleAsync(new GetKnowledgeDocumentQuery { DocumentId = documentId.Value });

        // Assert
        Assert.NotNull(result);
        Assert.Equal("ADR-7: prefer the outbox pattern", result.Content);
    }

    [Fact]
    public async Task HandleAsync_MissingDocument_ReturnsNull()
    {
        // Arrange
        var mockRepo = new Mock<IKnowledgeDocumentRepository>();
        mockRepo.Setup(r => r.GetByIdAsync(It.IsAny<DocumentId>(), default))
                .ReturnsAsync((KnowledgeDocument?)null);

        var handler = new GetKnowledgeDocumentQueryHandler(mockRepo.Object);

        // Act
        var result = await handler.HandleAsync(new GetKnowledgeDocumentQuery { DocumentId = Guid.NewGuid() });

        // Assert
        Assert.Null(result);
    }
}
```

Always test the null/not-found path. Handlers that assume the entity exists will throw `NullReferenceException` in production.

---

## Testing a Facade Consumer

Code that depends on `IKnowledgeContextFacade` should mock the facade interface, not reach for a repository:

```csharp
public class KnowledgeLookupServiceTests
{
    [Fact]
    public async Task LookUpAsync_WithResults_ReturnsSnippets()
    {
        // Arrange
        var mockKnowledge = new Mock<IKnowledgeContextFacade>();

        mockKnowledge.Setup(k => k.SearchKnowledgeAsync(
                It.IsAny<string>(), It.IsAny<ScopeId?>(), It.IsAny<int>(), default))
            .ReturnsAsync(new List<KnowledgeSnippetDto>
            {
                new()
                {
                    DocumentId = Guid.NewGuid(),
                    Content = "Inventory Service reserves stock before Checkout Service confirms payment",
                    RelevanceScore = 0.9,
                    Source = "architecture-notes"
                }
            });

        var lookup = new KnowledgeLookupService(mockKnowledge.Object);

        // Act
        var results = await lookup.LookUpAsync("stock reservation order", CancellationToken.None);

        // Assert
        Assert.Contains(results, r => r.Content.Contains("Inventory Service"));
    }
}
```

---

## Testing Error Paths

Handlers should propagate domain exceptions cleanly. Use `ThrowsAsync` to simulate infrastructure failures.

```csharp
[Fact]
public async Task HandleAsync_RepositoryFails_PropagatesException()
{
    // Arrange
    var mockRepo = new Mock<IKnowledgeDocumentRepository>();
    mockRepo.Setup(r => r.AddAsync(It.IsAny<KnowledgeDocument>(), default))
            .ThrowsAsync(new InvalidOperationException("DB unavailable"));
    var mockEmbedding = new Mock<IEmbeddingService>();
    mockEmbedding.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), default))
                 .ReturnsAsync(Embedding.Create(new float[EmbeddingDimensions.Default]));

    var handler = new AddKnowledgeDocumentCommandHandler(mockRepo.Object, mockEmbedding.Object);

    // Act & Assert
    await Assert.ThrowsAsync<InvalidOperationException>(
        () => handler.HandleAsync(new AddKnowledgeDocumentCommand { Content = "architecture-notes", ScopeId = Guid.NewGuid() }));

    // Verify the attempt was made
    mockRepo.Verify(r => r.AddAsync(It.IsAny<KnowledgeDocument>(), default), Times.Once);
}
```

---

## Simulating Transient Failure Then Recovery

To test fallback/retry behavior, use a `.Callback()` closure with a captured counter — the mock stays stateful across calls:

```csharp
var callCount = 0;
mockGraphRepository
    .Setup(r => r.GetFactsBySubjectAsync(It.IsAny<string>(), It.IsAny<ScopeId>(), It.IsAny<CancellationToken>()))
    .Callback(() =>
    {
        callCount++;
        if (callCount == 1) throw new InvalidOperationException("Connection reset");
    })
    .ReturnsAsync(new List<KnowledgeFact>());
// First call throws, second (fallback) succeeds.
```

---

## Special Case: Mock<IMediator> via Scope Factory

Handlers depend on repositories directly, never on `IMediator` — so most tests never mock it. A `Mock<IMediator>` only appears where a domain-event dispatcher resolves `IMediator` from a service scope. Mock the whole chain:

```csharp
var mockScope = new Mock<IServiceScope>();
var mockServiceProvider = new Mock<IServiceProvider>();
mockServiceProvider
    .Setup(sp => sp.GetService(typeof(IMediator)))
    .Returns(_mockMediator.Object);
mockScope.Setup(s => s.ServiceProvider).Returns(mockServiceProvider.Object);
_mockScopeFactory.Setup(f => f.CreateScope()).Returns(mockScope.Object);
```

If you find yourself mocking `IMediator` in a handler or facade test, the design is probably wrong — see the **cqrs** skill.

---

## Running and Filtering Tests

```bash
# Run all unit tests
dotnet test tests/Dragonmind.Knowledge.UnitTests

# Run a specific test class
dotnet test --filter "FullyQualifiedName~AddKnowledgeDocumentCommandHandlerTests"

# Run a specific test method
dotnet test --filter "FullyQualifiedName~AddKnowledgeDocumentCommandHandlerTests.HandleAsync_ValidCommand_StoresDocumentAndReturnsDto"

# Run with verbose output to see mock failures
dotnet test --logger "console;verbosity=detailed"
```

Validate after writing tests — run them before considering the work done. See the **dotnet** skill for build and test commands.

---

## Checklist: Adding Handler Tests

Copy this checklist when adding unit tests for a new command or query handler:

- [ ] Identify the handler's constructor dependencies (all should be interfaces)
- [ ] Create `Mock<T>` for each interface dependency
- [ ] Write happy-path test: setup mocks, execute handler, assert return value, verify repo calls
- [ ] Write not-found/null test (query handlers only): setup repo to return null, assert null result
- [ ] Write error-path test: setup repo to throw, assert exception propagates
- [ ] Verify `Times.Once` on the primary repository write call in command handler tests
- [ ] Verify `Times.Never` on write calls in query handler tests
- [ ] Reuse the Bundle pattern (private record + static `Build...()` factory) if the SUT has 3+ collaborators — see patterns.md
- [ ] Run tests: `dotnet test --filter "FullyQualifiedName~YourHandlerTests"`
- [ ] All tests pass before marking complete

---

## DO/DON'T Summary

| DO | DON'T |
|----|-------|
| Mock `IKnowledgeDocumentRepository` | Mock `EfCoreKnowledgeDocumentRepository` |
| Use `It.IsAny<CancellationToken>()` | Use exact `CancellationToken.None` in matchers |
| Create fresh `Mock<T>` per test | Share mock instances across tests |
| `Verify` repository side effects | Skip verification on write operations |
| Test null/not-found paths explicitly | Assume happy path is sufficient |
| Use `.Is<T>()` predicates for semantic assertions | Match on internal ID values |
