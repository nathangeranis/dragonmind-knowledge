---
name: moq
description: |
  Creates Moq mock objects for dependency injection testing in this DDD/CQRS Knowledge context.
  Use when: writing unit tests for CQRS command/query handlers, mocking the facade interface,
  setting up repository mocks, testing domain services with external dependencies, or verifying
  interactions between handlers and their dependencies.
allowed-tools: Read, Edit, Write, Glob, Grep, Bash
---

# Moq Skill

This project uses Moq 4.20.x with xUnit for all unit tests. Every CQRS handler test follows the same structure: mock the repository interface, construct the handler directly with the mock, exercise the handler, assert results. The facade is tested by mocking `IKnowledgeContextFacade` — never the concrete implementation. See the **xunit** skill for test structure conventions.

## Quick Start

### Single-repository command handler test (canonical shape)

This is the dominant shape across the test suite: a single `_mockRepository` field (singular name, not interface-specific), handler constructed in the test class constructor, `It.Is<T>(...)` predicate in `Verify()`.

```csharp
public class AddKnowledgeDocumentCommandHandlerTests
{
    private readonly Mock<IKnowledgeDocumentRepository> _mockRepository;
    private readonly Mock<IEmbeddingService> _mockEmbedding;
    private readonly AddKnowledgeDocumentCommandHandler _handler;

    public AddKnowledgeDocumentCommandHandlerTests()
    {
        _mockRepository = new Mock<IKnowledgeDocumentRepository>();
        _mockEmbedding = new Mock<IEmbeddingService>();
        _handler = new AddKnowledgeDocumentCommandHandler(_mockRepository.Object, _mockEmbedding.Object);
    }

    [Fact]
    public async Task HandleAsync_WithValidCommand_PersistsDocument()
    {
        // Arrange
        _mockEmbedding
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Embedding.Create(new float[EmbeddingDimensions.Default]));
        _mockRepository
            .Setup(r => r.AddAsync(It.IsAny<KnowledgeDocument>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var command = new AddKnowledgeDocumentCommand { Content = "architecture-notes: Checkout Service", ScopeId = Guid.NewGuid() };

        // Act
        var result = await _handler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.Equal(command.Content, result.Content);
        _mockRepository.Verify(
            r => r.AddAsync(It.Is<KnowledgeDocument>(d => d.Content.Value == command.Content), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
```

### Not-found path: repository returns null, verify no write

```csharp
[Fact]
public async Task HandleAsync_WithNonExistentDocument_ReturnsFalse()
{
    // Arrange
    var command = new DeleteKnowledgeDocumentCommand { DocumentId = Guid.NewGuid() };

    _mockRepository
        .Setup(r => r.GetByIdAsync(It.IsAny<DocumentId>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync((KnowledgeDocument?)null);

    // Act
    var result = await _handler.HandleAsync(command, CancellationToken.None);

    // Assert
    Assert.False(result);
    _mockRepository.Verify(
        r => r.DeleteAsync(It.IsAny<DocumentId>(), It.IsAny<CancellationToken>()),
        Times.Never);
}
```

### Mock the facade

```csharp
var mockKnowledgeFacade = new Mock<IKnowledgeContextFacade>();
// The facade interface lives in Dragonmind.Core/Application/AntiCorruptionLayer/ — always mock
// the interface, never the concrete facade or the repository underneath it.
```

## Key Concepts

| Concept | Usage | Example |
|---------|-------|---------|
| `Mock<T>` | Create mock of interface | `new Mock<IKnowledgeDocumentRepository>()` |
| `.Setup()` | Configure behavior | `.Setup(r => r.GetByIdAsync(...))` |
| `It.IsAny<T>()` | Match any argument | `It.IsAny<CancellationToken>()` |
| `.ReturnsAsync()` | Return async value | `.ReturnsAsync(document)` |
| `.Verify()` | Assert interaction | `.Verify(r => r.AddAsync(...), Times.Once)` |
| `.Object` | Get the mock instance | `new Handler(mockRepo.Object)` |

## Common Patterns

### Repository returning a domain aggregate

Build aggregates through their real static factories — never mock aggregates:

```csharp
var document = KnowledgeDocument.Create(ScopeId.New(), DocumentContent.Create("runbook: Payments DB failover"));
mockRepo.Setup(r => r.GetByIdAsync(It.IsAny<DocumentId>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(document);
```

### Verifying no interaction occurred

```csharp
mockRepo.Verify(r => r.DeleteAsync(It.IsAny<DocumentId>(), default), Times.Never);
```

### Throwing from a mock (error path)

```csharp
mockRepo.Setup(r => r.AddAsync(It.IsAny<KnowledgeDocument>(), default))
        .ThrowsAsync(new InvalidOperationException("Duplicate document"));
```

## See Also

- [patterns](references/patterns.md)
- [workflows](references/workflows.md)

## Related Skills

- **xunit** — test structure, `[Fact]`, `[Theory]`, assertions used alongside Moq
- **csharp** — nullable reference types, async/await, records used in command/query objects
- **dotnet** — running tests with `dotnet test`, filter syntax
- **ddd** — aggregate factory methods used in mock return values
- **cqrs** — handler + command/query types being tested
