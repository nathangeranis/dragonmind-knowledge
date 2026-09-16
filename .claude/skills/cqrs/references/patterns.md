# CQRS Patterns Reference

## Contents
- Command Structure
- Query Structure
- Facade Pattern
- Validation Pipeline Behavior
- Anti-Patterns
- How the Facade Is Consumed

---

## Command Structure

Commands are `sealed record` with `init` properties. Always use `[Required]` on non-optional inputs.

```csharp
// ✅ CORRECT — immutable, validated, clear ownership
public sealed record CreateKnowledgeFactCommand : ICommand<KnowledgeFactDto?>
{
    [Required]
    public required Guid ScopeId { get; init; }
    [Required]
    public required string SubjectName { get; init; }
    [Required]
    public required string SubjectType { get; init; }
    [Required]
    public required string Predicate { get; init; }
    [Required]
    public required string ObjectName { get; init; }
    [Required]
    public required string ObjectType { get; init; }
}
```

Handler receives the command and coordinates domain logic — including rejecting input that fails a domain invariant before anything is persisted:

```csharp
public sealed class CreateKnowledgeFactCommandHandler
    : ICommandHandler<CreateKnowledgeFactCommand, KnowledgeFactDto?>
{
    private readonly IKnowledgeGraphRepository _repository;

    public CreateKnowledgeFactCommandHandler(IKnowledgeGraphRepository repository)
    {
        _repository = repository;
    }

    public async Task<KnowledgeFactDto?> HandleAsync(
        CreateKnowledgeFactCommand command,
        CancellationToken ct = default)
    {
        var scopeId = ScopeId.From(command.ScopeId);
        var predicate = RelationshipTypes.Normalize(command.Predicate);
        if (!RelationshipTypes.IsAllowed(predicate))
        {
            // Write nothing; the caller sees a null result.
            return null;
        }

        var fact = KnowledgeFact.CreateFromStrings(
            scopeId, command.SubjectName, command.SubjectType, predicate, command.ObjectName, command.ObjectType);
        await _repository.AddAsync(fact, ct);
        return new KnowledgeFactDto
        {
            FactId = fact.Id.Value,
            ScopeId = fact.ScopeId.Value,
            Subject = fact.Subject.Name,
            SubjectType = fact.Subject.EntityType,
            Predicate = fact.Predicate,
            Object = fact.Object.Name,
            ObjectType = fact.Object.EntityType,
            Timestamp = fact.Timestamp
        };
    }
}
```

---

## Query Structure

Queries are read-only. Handlers map aggregates to DTOs — **never return domain aggregates**. Note
that `SearchKnowledgeQueryHandler` returns `VectorSearchResultDto`, not `KnowledgeSnippetDto` — the
public `KnowledgeSnippetDto` shape is a facade-level concern (see *Facade Pattern* below).

```csharp
public sealed record SearchKnowledgeQuery : IQuery<IReadOnlyList<VectorSearchResultDto>>
{
    [Required]
    public required string QueryText { get; init; }
    public Guid? ScopeId { get; init; }
    public int MaxResults { get; init; } = 5;
    public double MinSimilarity { get; init; } = 0.0;
}

public sealed class SearchKnowledgeQueryHandler
    : IQueryHandler<SearchKnowledgeQuery, IReadOnlyList<VectorSearchResultDto>>
{
    private readonly IVectorSearchService _vectorSearchService;

    public SearchKnowledgeQueryHandler(IVectorSearchService vectorSearchService)
        => _vectorSearchService = vectorSearchService;

    public async Task<IReadOnlyList<VectorSearchResultDto>> HandleAsync(
        SearchKnowledgeQuery query,
        CancellationToken ct = default)
    {
        var scopeId = query.ScopeId is { } id ? ScopeId.From(id) : null;
        var results = await _vectorSearchService.SearchAsync(
            query.QueryText, query.MaxResults, query.MinSimilarity, scopeId, ct);
        return results.Select(r => new VectorSearchResultDto(
            new KnowledgeDocumentDto(
                r.Document.Id.Value, r.Document.ScopeId.Value, r.Document.Content.Value,
                r.Document.Content.Source, r.Document.Content.Category, r.Document.Timestamp,
                r.Document.Embedding != null),
            r.SimilarityScore)).ToList();
    }
}
```

---

## Facade Pattern

The facade is the Anti-Corruption Layer. It injects `IMediator` and dispatches all commands/queries via `_mediator.Send()`. Handlers are auto-discovered by MediatR assembly scanning — they are **not individually registered in DI** and are **not resolved from `IServiceProvider`**.

```csharp
public sealed class KnowledgeContextFacade : IKnowledgeContextFacade
{
    private readonly IMediator _mediator;

    public KnowledgeContextFacade(IMediator mediator)
        => _mediator = mediator;

    // The only place VectorSearchResultDto becomes the public KnowledgeSnippetDto shape —
    // the query handler above never produces a KnowledgeSnippetDto itself.
    public async Task<IReadOnlyList<KnowledgeSnippetDto>> SearchKnowledgeAsync(
        string query, ScopeId? scopeId, int maxResults = 5, CancellationToken ct = default)
    {
        var results = await _mediator.Send(new SearchKnowledgeQuery(query, maxResults, scopeId: scopeId?.Value), ct);
        return results.Select(r => new KnowledgeSnippetDto
        {
            DocumentId = r.Document.DocumentId,
            Content = r.Document.Content,
            RelevanceScore = r.SimilarityScore,
            Source = r.Document.Source ?? "Unknown"
        }).ToList();
    }

    public async Task<bool> AddKnowledgeFactAsync(
        string subject, string predicate, string @object, ScopeId scopeId,
        string subjectType = "Entity", string objectType = "Entity", CancellationToken ct = default)
    {
        var result = await _mediator.Send(new CreateKnowledgeFactCommand
        {
            ScopeId = scopeId.Value,
            SubjectName = subject,
            SubjectType = subjectType,
            Predicate = predicate,
            ObjectName = @object,
            ObjectType = objectType
        }, ct);
        return result is not null;
    }
}
```

The facade interface lives in `Dragonmind.Core/Application/AntiCorruptionLayer/`. The implementation lives in `Dragonmind.Knowledge/Infrastructure/AntiCorruptionLayer/`.

**`IServiceProvider` has no legitimate use inside the facade or a handler.** Every dependency the facade needs is resolvable through its own constructor; a facade that reaches for `IServiceProvider.GetRequiredService<T>()` instead is hiding a dependency the constructor should declare.

---

## Validation Pipeline Behavior

`[Required]`/`[Range]`/`[MaxLength]` attributes on commands/queries aren't decorative — they're
enforced by `ValidationPipelineBehavior<TRequest, TResponse>`
(`Dragonmind.Core/Application/Behaviors/ValidationPipelineBehavior.cs`), a MediatR open pipeline
behavior registered in DI:

```csharp
cfg.AddOpenBehavior(typeof(Dragonmind.Core.Application.Behaviors.LoggingPipelineBehavior<,>));
cfg.AddOpenBehavior(typeof(Dragonmind.Core.Application.Behaviors.ValidationPipelineBehavior<,>));
```

It runs before every handler, calls `Validator.TryValidateObject` on the request, and throws
`ValidationException` if any attribute fails — the handler never executes with invalid data.

```csharp
public async Task<TResponse> Handle(
    TRequest request,
    RequestHandlerDelegate<TResponse> next,
    CancellationToken cancellationToken)
{
    var validationResults = new List<ValidationResult>();
    var validationContext = new ValidationContext(request);

    if (!Validator.TryValidateObject(request, validationContext, validationResults, validateAllProperties: true))
    {
        var requestName = typeof(TRequest).Name;
        var errors = string.Join("; ", validationResults.Select(r => r.ErrorMessage));

        _logger.LogWarning("Validation failed for {RequestName}: {Errors}", requestName, errors);

        throw new ValidationException($"Invalid request: {errors}");
    }

    return await next();
}
```

**Practical implication:** a `ValidationException` you didn't throw yourself almost always means
a DataAnnotation failed on a command/query — check the attributes on the request record before
looking at the handler.

---

## WARNING: Direct Repository Access in the Facade

**The Problem:**

```csharp
// BAD — bypasses CQRS, untestable, violates architectural boundaries
public async Task<bool> AddKnowledgeFactAsync(string subject, ...)
{
    var repo = _serviceProvider.GetRequiredService<IKnowledgeGraphRepository>();
    var fact = KnowledgeFact.CreateFromStrings(scopeId, subject, subjectType, predicate, @object, objectType);
    await repo.AddAsync(fact);
    return true;
}
```

**Why This Breaks:**
1. Business logic leaks into the ACL layer — now two places to maintain
2. Unit testing the facade requires mocking the repository, not just the handler
3. Validation, domain events, and pre/post logic in the handler are silently skipped
4. Undermines CQRS compliance — the entire pattern loses its value

**The Fix:** Always go through the handler as shown in the Facade Pattern section above.

---

## WARNING: Manually Registering Handlers in DI

**The Problem:**

```csharp
// BAD — handlers should not be registered manually
services.AddTransient<AddKnowledgeDocumentCommandHandler>(); // ❌ unnecessary
services.AddScoped<AddKnowledgeDocumentCommandHandler>();    // ❌ wrong lifetime AND unnecessary
services.AddSingleton<AddKnowledgeDocumentCommandHandler>(); // ❌ wrong lifetime AND unnecessary
```

**Why This Breaks:**
1. Handlers are auto-discovered by MediatR assembly scanning — manual registration is redundant and can cause double-resolution
2. Registering at wrong lifetimes (Scoped/Singleton) causes captive dependency — the handler (and its DbContext) may outlive the request
3. Shared DbContext across requests causes EF Core tracking conflicts and concurrency exceptions

**The Fix:**

```csharp
// GOOD — let MediatR discover handlers via assembly scanning
// Only register repositories, domain services, and the facade in the context DI extension.
// IKnowledgeContextFacade always resolves to the caching decorator wrapping the concrete facade —
// see "Facade Pattern" above for the decorator wiring.
services.AddScoped<IKnowledgeDocumentRepository, EfCoreKnowledgeDocumentRepository>();
services.AddScoped<KnowledgeContextFacade>(); // Scoped, injects IMediator
```

---

## How the Facade Is Consumed

Everything outside this repository reaches the Knowledge context through `IKnowledgeContextFacade` — never through a handler, a query object, or a repository directly. The facade interface (`Dragonmind.Core/Application/AntiCorruptionLayer/IKnowledgeContextFacade.cs`) and its DTOs are the entire public surface; nothing on the other side of that boundary needs to know a command or a MediatR pipeline exists behind it.

```csharp
// ✅ CORRECT — a consumer depends only on the facade interface and its DTOs
public class ConsumingService
{
    private readonly IKnowledgeContextFacade _knowledge;

    public async Task<IReadOnlyList<KnowledgeSnippetDto>> LookUpAsync(string topic, CancellationToken ct)
    {
        return await _knowledge.SearchKnowledgeAsync(topic, scopeId: null, maxResults: 5, ct);
    }
}

// ❌ INCORRECT — never reach past the facade like this
public class ConsumingService
{
    private readonly SearchKnowledgeQueryHandler _handler; // Wrong! Internal to Dragonmind.Knowledge
}
```

See the **ddd** skill for why the facade is the only door, and `AclBoundaryTests` (`tests/Dragonmind.Knowledge.UnitTests`) for the reflection-based tests that enforce it.
