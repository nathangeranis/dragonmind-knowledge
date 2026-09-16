---
name: csharp
description: |
  Enforces C# 13 syntax, nullable reference types, and language conventions for this DDD/CQRS knowledge-retrieval service.
  Use when: writing commands, queries, handlers, aggregates, value objects, domain events, repositories, facades, or any C# class in this codebase.
allowed-tools: Read, Edit, Write, Glob, Grep, Bash
---

# C# Skill

This is a C# 13 / .NET 10 codebase with nullable reference types enabled everywhere (`<Nullable>enable</Nullable>` in every project). The language conventions here aren't cosmetic — they encode the DDD/CQRS architecture: `sealed record` for immutable Commands/Queries dispatched through MediatR, `sealed class` aggregates with private setters and factory methods, and `ValueObject`-derived types instead of primitives for domain identifiers. Getting these wrong doesn't just look sloppy, it breaks the CQRS bridge (`ICommand<T> : IRequest<T>`) or lets invalid domain state leak past validation.

## Quick Start

### Command/Query records

```csharp
public sealed record AddKnowledgeDocumentCommand : ICommand<KnowledgeDocumentDto>
{
    [Required]
    public required string Content { get; init; }
    [Required]
    public required Guid ScopeId { get; init; }
}
```

### Nullable-safe domain checks

```csharp
public string? Source { get; set; }
public string Content { get; set; } = string.Empty;

if (document?.ScopeId is null)
{
    // Handle documents with no scope
}
```

### Async all the way down

`SearchKnowledgeQueryHandler.HandleAsync` returns `VectorSearchResultDto`, not `KnowledgeSnippetDto` —
that public DTO is only produced later, by the facade (see the **cqrs** skill):

```csharp
public async Task<IReadOnlyList<VectorSearchResultDto>> HandleAsync(
    SearchKnowledgeQuery query, CancellationToken ct = default)
{
    var results = await _vectorSearchService.SearchAsync(
        query.QueryText, query.MaxResults, query.MinSimilarity, scopeId: null, ct);
    return results.Select(r => new VectorSearchResultDto(
        new KnowledgeDocumentDto(
            r.Document.Id.Value, r.Document.ScopeId.Value, r.Document.Content.Value,
            r.Document.Content.Source, r.Document.Content.Category, r.Document.Timestamp,
            r.Document.Embedding != null),
        r.SimilarityScore)).ToList();
}
```

## Key Concepts

| Concept | Usage | Example |
|---------|-------|---------|
| `sealed record` | Commands, Queries, DTOs — immutable, `init`-only | `AddKnowledgeDocumentCommand` |
| `required` modifier | Mandatory record/class properties, replaces constructor boilerplate | `public required string Content { get; init; }` |
| Nullable reference types | `?` marks optional; unmarked means "never null, enforced by compiler" | `string? Source` vs `string Content` |
| `sealed class` aggregate | Domain aggregate roots — private setters, factory methods, private ctor for EF Core | `KnowledgeDocument.Create(...)` |
| `ValueObject` | Strongly-typed IDs and domain concepts instead of primitives | `ScopeId.From(guid)` |
| Global usings | Per-project `GlobalUsings.cs`, no repeated `using` lines | see `references/patterns.md` |

## Common Patterns

### Factory methods over public constructors

**When:** Creating a new aggregate or value object that must raise a domain event or enforce invariants at construction.

```csharp
public static KnowledgeFact Create(ScopeId scopeId, GraphEntity subject, string predicate, GraphEntity @object)
{
    var fact = new KnowledgeFact { Id = FactId.New(), Subject = subject, Predicate = predicate, Object = @object };
    fact.AddDomainEvent(new KnowledgeFactCreatedDomainEvent(fact.Id, scopeId));
    return fact;
}
```

### Pattern matching over type-checking chains

**When:** Branching on a domain enum or classification result.

```csharp
var response = relationship switch
{
    _ when RelationshipTypes.IsAllowed(relationship) => Accept(relationship),
    _ => Reject(relationship)
};
```

## See Also

- [patterns](references/patterns.md)
- [workflows](references/workflows.md)

## Related Skills

- **dotnet** — project structure, global usings, build/test workflows this language skill assumes
- **cqrs** — the command/query record shapes documented here are dispatched via MediatR
- **ddd** — aggregate/value-object patterns shown here belong to the broader DDD skill
- **entity-framework** — private constructors and property conversions exist for EF Core mapping
- **xunit** — test method naming and `Assert` conventions pair with these C# idioms
- **moq** — mocking interfaces declared with the conventions in this skill
