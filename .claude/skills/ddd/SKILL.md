---
name: ddd
description: |
  Applies Domain-Driven Design aggregates, value objects, and the anti-corruption-layer boundary.
  Use when: adding aggregates/value objects/domain events, extending the Knowledge domain model, wiring the ACL facade, or reviewing whether a piece of logic belongs in the domain, application, or infrastructure layer.
allowed-tools: Read, Edit, Write, Glob, Grep, Bash
---

# DDD Skill

This repository is a single bounded context — `Dragonmind.Knowledge` — laid out with internal `Domain/`, `Application/`, `Infrastructure/` directories, not separate projects per layer. Domain logic has zero infrastructure dependencies. Everything outside the context reaches it exclusively through the `IKnowledgeContextFacade` Anti-Corruption Layer (ACL) facade, which dispatches via MediatR (see the **cqrs** skill for handler wiring). This skill covers the DDD tactical patterns: aggregates, value objects, domain events, and the boundary the facade enforces.

## Quick Start

### Aggregate with factory method

```csharp
public sealed class KnowledgeDocument : AggregateRoot<DocumentId>
{
    public DocumentContent Content { get; private set; } = null!;

    private KnowledgeDocument() { } // EF Core

    public static KnowledgeDocument Create(ScopeId scopeId, DocumentContent content)
    {
        var document = new KnowledgeDocument { Id = DocumentId.New(), Content = content };
        document.AddDomainEvent(new KnowledgeDocumentAddedDomainEvent(document.Id, scopeId));
        return document;
    }
}
```

### Value object as strongly-typed ID

```csharp
public sealed class DocumentId : ValueObject
{
    public Guid Value { get; }
    private DocumentId(Guid value) => Value = value;

    public static DocumentId New() => new(Guid.NewGuid());
    public static DocumentId From(Guid value) => new(value);

    protected override IEnumerable<object> GetEqualityComponents() { yield return Value; }
}
```

## Key Concepts

| Concept | Usage | Example |
|---------|-------|---------|
| Aggregate Root | Encapsulates invariants, exposes behavior not properties | `KnowledgeDocument`, `KnowledgeFact` |
| Value Object | Immutable, equality by value, no identity | `DocumentId`, `ScopeId`, `Embedding`, `DocumentContent` |
| Domain Event | Raised on significant state change, dispatched after persistence | `KnowledgeDocumentAddedDomainEvent` |
| ACL Facade | Only door into this context | `IKnowledgeContextFacade` |
| Repository Interface | Lives in `Domain/Repositories/`, implemented in `Infrastructure/` | `IKnowledgeDocumentRepository`, `IKnowledgeGraphRepository` |

## Common Patterns

### Reaching the context only through the facade

**When:** Anything outside `Dragonmind.Knowledge` needs to read or write knowledge.

```csharp
public class ConsumingService
{
    private readonly IKnowledgeContextFacade _knowledgeContext;

    public async Task<IReadOnlyList<KnowledgeSnippetDto>> LookUpAsync(string topic)
    {
        return await _knowledgeContext.SearchKnowledgeAsync(topic, scopeId: null, maxResults: 5);
    }
}
```

### Domain event for state-change notification

**When:** An aggregate's state change should be observable to code outside the method that caused it, without that code polling for the change.

```csharp
public sealed record KnowledgeDocumentAddedDomainEvent(DocumentId DocumentId, ScopeId? ScopeId) : DomainEvent;
```

## See Also

- [patterns](references/patterns.md)
- [workflows](references/workflows.md)

## Related Skills

- **cqrs** — commands/queries/handlers that operate on these aggregates
- **csharp** — nullable reference types, records, sealed classes used throughout
- **entity-framework** — persisting aggregates and value object conversions
- **dotnet** — project/csproj structure
