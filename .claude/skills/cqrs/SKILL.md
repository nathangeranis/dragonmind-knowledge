---
name: cqrs
description: |
  Implements CQRS command handlers and query handlers for this DDD knowledge-retrieval service.
  Use when: adding commands (state-changing operations) or queries (read-only operations), wiring up handlers in DI, updating the facade to expose new operations, or fixing CQRS compliance violations where repository access bypasses handlers.
allowed-tools: Read, Edit, Write, Glob, Grep, Bash
---

# CQRS Skill

The Knowledge context implements CQRS via `ICommand<T>`/`IQuery<T>` interfaces from `Dragonmind.Core`. `ICommand<T>` extends MediatR's `IRequest<T>` and `ICommandHandler<T,R>` extends `IRequestHandler<T,R>` via a default interface bridge in `Dragonmind.Core/Application/CQRS.cs`. The facade injects `IMediator` and dispatches via `_mediator.Send()` — handlers are auto-discovered by MediatR assembly scanning and are **not individually registered in DI**.

## Quick Start

### Add a Command

```csharp
// Dragonmind.Knowledge/Application/Commands/AddKnowledgeDocument/AddKnowledgeDocumentCommand.cs
public sealed record AddKnowledgeDocumentCommand : ICommand<KnowledgeDocumentDto>
{
    [Required]
    public required string Content { get; init; }
    [Required]
    public required Guid ScopeId { get; init; }
    public string? Source { get; init; }
}

// AddKnowledgeDocumentCommandHandler.cs (same directory)
public sealed class AddKnowledgeDocumentCommandHandler
    : ICommandHandler<AddKnowledgeDocumentCommand, KnowledgeDocumentDto>
{
    private readonly IKnowledgeDocumentRepository _repository;
    private readonly IEmbeddingService _embeddingService;

    public AddKnowledgeDocumentCommandHandler(IKnowledgeDocumentRepository repository, IEmbeddingService embeddingService)
    {
        _repository = repository;
        _embeddingService = embeddingService;
    }

    public async Task<KnowledgeDocumentDto> HandleAsync(AddKnowledgeDocumentCommand command, CancellationToken ct = default)
    {
        var scopeId = ScopeId.From(command.ScopeId);
        var content = DocumentContent.Create(command.Content, command.Source);
        var embedding = await _embeddingService.GenerateEmbeddingAsync(command.Content, ct);
        var document = KnowledgeDocument.CreateWithEmbedding(scopeId, content, embedding);
        await _repository.AddAsync(document, ct);
        return new KnowledgeDocumentDto(
            document.Id.Value, document.ScopeId.Value, document.Content.Value,
            document.Content.Source, document.Content.Category, document.Timestamp, hasEmbedding: true);
    }
}
```

### Add a Query

The internal query handler returns `VectorSearchResultDto` (a `KnowledgeDocumentDto` plus a
similarity score) — **not** `KnowledgeSnippetDto`. Only the facade (below) maps that internal DTO
onto the public `KnowledgeSnippetDto` shape.

```csharp
// Dragonmind.Knowledge/Application/Queries/SearchKnowledge/SearchKnowledgeQuery.cs
public sealed record SearchKnowledgeQuery : IQuery<IReadOnlyList<VectorSearchResultDto>>
{
    [Required]
    public required string QueryText { get; init; }
    public Guid? ScopeId { get; init; }
    public int MaxResults { get; init; } = 5;
}

// SearchKnowledgeQueryHandler.cs (same directory)
public sealed class SearchKnowledgeQueryHandler : IQueryHandler<SearchKnowledgeQuery, IReadOnlyList<VectorSearchResultDto>>
{
    private readonly IVectorSearchService _vectorSearchService;

    public SearchKnowledgeQueryHandler(IVectorSearchService vectorSearchService)
        => _vectorSearchService = vectorSearchService;

    public async Task<IReadOnlyList<VectorSearchResultDto>> HandleAsync(SearchKnowledgeQuery query, CancellationToken ct = default)
    {
        var scopeId = query.ScopeId is { } id ? ScopeId.From(id) : null;
        var results = await _vectorSearchService.SearchAsync(query.QueryText, query.MaxResults, minSimilarity: 0.0, scopeId, ct);
        return results.Select(r => new VectorSearchResultDto(
            new KnowledgeDocumentDto(
                r.Document.Id.Value, r.Document.ScopeId.Value, r.Document.Content.Value,
                r.Document.Content.Source, r.Document.Content.Category, r.Document.Timestamp,
                r.Document.Embedding != null),
            r.SimilarityScore)).ToList();
    }
}
```

### Register in DI

Handlers are **not registered individually** — MediatR discovers them via assembly scanning. Only repositories, domain services, and the facade are registered.

```csharp
// Infrastructure/DI/KnowledgeContextServiceExtensions.cs
public static IServiceCollection AddKnowledgeContext(this IServiceCollection services, NpgsqlDataSource dataSource, Action<KnowledgeOptions>? configure = null)
{
    services.AddScoped<IKnowledgeDocumentRepository, EfCoreKnowledgeDocumentRepository>();

    // IKnowledgeContextFacade always resolves to the caching decorator — the concrete facade
    // is registered too, but only so the decorator's factory can pull it in as `inner`.
    services.AddScoped<KnowledgeContextFacade>();
    services.AddScoped<IKnowledgeContextFacade>(provider => new CachingKnowledgeContextFacade(
        provider.GetRequiredService<KnowledgeContextFacade>(),
        provider.GetRequiredService<IKnowledgeCacheService>(),
        provider.GetRequiredService<ILogger<CachingKnowledgeContextFacade>>()));

    // NOTE: AddKnowledgeDocumentCommandHandler and SearchKnowledgeQueryHandler are NOT registered here.
    // MediatR discovers them automatically via assembly scanning.
    return services;
}
```

### Expose via Facade

```csharp
// Infrastructure/AntiCorruptionLayer/KnowledgeContextFacade.cs
public sealed class KnowledgeContextFacade : IKnowledgeContextFacade
{
    private readonly IMediator _mediator;

    public KnowledgeContextFacade(IMediator mediator) => _mediator = mediator;

    public async Task<DocumentId> StoreKnowledgeAsync(string content, string source, ScopeId scopeId, CancellationToken ct = default)
    {
        var result = await _mediator.Send(new AddKnowledgeDocumentCommand { Content = content, Source = source, ScopeId = scopeId.Value }, ct);
        return DocumentId.From(result.DocumentId);
    }
}
```

## Key Rules

| Rule | Reason |
|------|--------|
| Handlers auto-discovered by MediatR | No `AddTransient<Handler>()` needed — the host scans assemblies |
| Facade injects `IMediator`, calls `_mediator.Send()` | Standard MediatR dispatch; avoids captive dependency |
| Commands = `sealed record` | Immutability, structural equality, clear intent |
| `[Required]` on mandatory props | Enforced by `ValidationPipelineBehavior` before the handler runs |
| No repo access in the facade | Bypasses CQRS, breaks testability |

## See Also

- [patterns](references/patterns.md)
- [workflows](references/workflows.md)

## Related Skills

- See the **csharp** skill for C# record/sealed class patterns
- See the **dotnet** skill for DI registration and service lifetimes
- See the **entity-framework** skill for repository implementations
- See the **xunit** skill for testing handlers with Moq
- See the **moq** skill for mocking `IMediator` and repositories
- See the **ddd** skill for aggregate and domain event patterns
