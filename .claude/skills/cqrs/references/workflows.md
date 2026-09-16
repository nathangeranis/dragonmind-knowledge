# CQRS Workflows Reference

## Contents
- Adding a New Command End-to-End
- Adding a New Query End-to-End
- Fixing CQRS Compliance Violations
- Testing Handlers
- Checklist

---

## Adding a New Command End-to-End

**Scenario:** Add a `DeleteKnowledgeDocument` command.

**Step 1 — Create the command and handler:**

```csharp
// Dragonmind.Knowledge/Application/Commands/DeleteKnowledgeDocument/DeleteKnowledgeDocumentCommand.cs
public sealed record DeleteKnowledgeDocumentCommand : ICommand<bool>
{
    [Required]
    public required Guid DocumentId { get; init; }
}

// DeleteKnowledgeDocumentCommandHandler.cs
public sealed class DeleteKnowledgeDocumentCommandHandler : ICommandHandler<DeleteKnowledgeDocumentCommand, bool>
{
    private readonly IKnowledgeDocumentRepository _repository;

    public DeleteKnowledgeDocumentCommandHandler(IKnowledgeDocumentRepository repository)
        => _repository = repository;

    public async Task<bool> HandleAsync(DeleteKnowledgeDocumentCommand command, CancellationToken ct = default)
    {
        var document = await _repository.GetByIdAsync(DocumentId.From(command.DocumentId), ct);
        if (document is null)
        {
            return false;
        }
        await _repository.DeleteAsync(document.Id, ct);
        return true;
    }
}
```

**Step 2 — No DI registration needed:** MediatR discovers `DeleteKnowledgeDocumentCommandHandler` automatically via assembly scanning. Do not add `services.AddTransient<DeleteKnowledgeDocumentCommandHandler>()`.

**Step 3 — Expose via the facade** (if the operation needs to cross the ACL boundary):

```csharp
// In KnowledgeContextFacade.cs
public async Task<bool> DeleteKnowledgeDocumentAsync(Guid documentId, CancellationToken ct = default)
{
    return await _mediator.Send(new DeleteKnowledgeDocumentCommand { DocumentId = documentId }, ct);
}
```

**Step 4 — Add to the facade interface** (`Dragonmind.Core/Application/AntiCorruptionLayer/IKnowledgeContextFacade.cs`):

```csharp
Task<bool> DeleteKnowledgeDocumentAsync(Guid documentId, CancellationToken ct = default);
```

**Step 5 — Validate:** `dotnet build Dragonmind.Knowledge.sln` — confirm no interface implementation errors.

---

## Adding a New Query End-to-End

**Scenario:** Add a `GetDocumentCount` query returning a scope-scoped count.

```csharp
// Dragonmind.Knowledge/Application/Queries/GetDocumentCount/GetDocumentCountQuery.cs
public sealed record GetDocumentCountQuery : IQuery<int>
{
    public ScopeId? ScopeId { get; init; }
}

// GetDocumentCountQueryHandler.cs
public sealed class GetDocumentCountQueryHandler : IQueryHandler<GetDocumentCountQuery, int>
{
    private readonly IKnowledgeDocumentRepository _repository;

    public GetDocumentCountQueryHandler(IKnowledgeDocumentRepository repository)
        => _repository = repository;

    public async Task<int> HandleAsync(GetDocumentCountQuery query, CancellationToken ct = default)
    {
        return await _repository.CountAsync(query.ScopeId, ct);
    }
}
```

No registration needed: MediatR discovers `GetDocumentCountQueryHandler` automatically.

---

## Fixing CQRS Compliance Violations

The facade must dispatch via `IMediator.Send()`. Here's the fix pattern if it incorrectly accesses a repository or resolves a handler from `IServiceProvider`:

```csharp
// BEFORE — incorrect: direct repository access or manual handler resolution
public async Task<KnowledgeDocumentDto?> GetDocumentAsync(Guid documentId)
{
    var repo = _serviceProvider.GetRequiredService<IKnowledgeDocumentRepository>();
    var document = await repo.GetByIdAsync(DocumentId.From(documentId));
    return document is null ? null : new KnowledgeDocumentDto(
        document.Id.Value, document.ScopeId.Value, document.Content.Value,
        document.Content.Source, document.Content.Category, document.Timestamp, document.Embedding != null);
}

// AFTER — correct: dispatch via IMediator
public async Task<KnowledgeDocumentDto?> GetDocumentAsync(Guid documentId, CancellationToken ct = default)
{
    return await _mediator.Send(new GetKnowledgeDocumentQuery { DocumentId = documentId }, ct);
}
```

**Iterate-until-pass for CQRS fixes:**
1. Convert one facade method to use `_mediator.Send()`
2. Run `dotnet build Dragonmind.Knowledge.sln` — confirm compilation
3. Run `dotnet test tests/Dragonmind.Knowledge.UnitTests` — confirm no regressions
4. If tests fail, fix the DTO mapping and repeat step 3
5. Only proceed to the next method when tests pass

---

## Testing Handlers

Unit tests for handlers mock repositories — no database required. See the **xunit** and **moq** skills for full patterns.

```csharp
[Fact]
public async Task DeleteKnowledgeDocumentCommandHandler_ExistingDocument_ReturnsTrue()
{
    // Arrange
    var documentId = DocumentId.New();
    var document = KnowledgeDocument.Create(ScopeId.New(), DocumentContent.Create("architecture-notes"));

    var mockRepo = new Mock<IKnowledgeDocumentRepository>();
    mockRepo.Setup(r => r.GetByIdAsync(documentId, default))
            .ReturnsAsync(document);

    var handler = new DeleteKnowledgeDocumentCommandHandler(mockRepo.Object);
    var command = new DeleteKnowledgeDocumentCommand { DocumentId = documentId.Value };

    // Act
    var result = await handler.HandleAsync(command);

    // Assert
    Assert.True(result);
    mockRepo.Verify(r => r.DeleteAsync(document.Id, default), Times.Once);
}
```

Test location: `tests/Dragonmind.Knowledge.UnitTests/`

Run: `dotnet test tests/Dragonmind.Knowledge.UnitTests`

---

## Checklist: Adding Command or Query

Copy and track progress:

- [ ] Create `{Operation}Command.cs` or `{Operation}Query.cs` in `Application/Commands|Queries/{OperationName}/`
- [ ] Create `{Operation}CommandHandler.cs` or `{Operation}QueryHandler.cs` in same directory
- [ ] Implement `ICommand<TResult>` / `IQuery<TResult>` on the message
- [ ] Implement `ICommandHandler` / `IQueryHandler` on the handler
- [ ] **No DI handler registration needed** — MediatR auto-discovers the handler via assembly scanning
- [ ] (If it crosses the ACL boundary) Add a method to `KnowledgeContextFacade` using `_mediator.Send(new {Operation}Command { ... })`
- [ ] (If it crosses the ACL boundary) Add the method signature to `IKnowledgeContextFacade` in `Dragonmind.Core`
- [ ] Write a unit test for the handler with a mocked repository
- [ ] `dotnet build Dragonmind.Knowledge.sln` — zero errors
- [ ] `dotnet test tests/Dragonmind.Knowledge.UnitTests` — all pass
