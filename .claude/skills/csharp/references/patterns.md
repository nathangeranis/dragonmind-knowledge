# C# Patterns Reference

## Contents
- Records for Commands/Queries
- Nullable Reference Type Discipline
- Value Objects Instead of Primitives
- Sealed Aggregates with Private Setters
- Dependency Injection via Constructors
- Anti-Patterns

## Records for Commands/Queries

Every command and query is a `sealed record` implementing `ICommand<T>`/`IQuery<T>`. Records give you value equality, immutability via `init`, and a compact syntax — exactly what a DTO crossing the MediatR pipeline needs.

```csharp
public sealed record GetRelatedFactsQuery : IQuery<IReadOnlyList<KnowledgeFactDto>>
{
    [Required]
    public required string EntityName { get; init; }
    [Required]
    public required ScopeId ScopeId { get; init; }
    public int MaxDepth { get; init; } = 2;
}
```

**DO** use `required` + `init` for mandatory properties instead of constructor parameters:

```csharp
// GOOD - required forces callers to set it, init prevents later mutation
public sealed record MyCommand : ICommand<MyResult>
{
    [Required]
    public required string Name { get; init; }
}
```

**DON'T** give commands mutable properties:

```csharp
// BAD - allows a handler or middleware to mutate the command mid-pipeline
public sealed record MyCommand : ICommand<MyResult>
{
    public string Name { get; set; } = string.Empty;
}
```

**Why This Breaks:** A command is a snapshot of caller intent taken at dispatch time. `set` accessors let any code holding a reference to the command mutate it after MediatR has already started routing it — including inside a handler that runs before another handler in a pipeline. That produces non-reproducible bugs where the same command object behaves differently depending on call order. `init` closes that door at compile time.

## Nullable Reference Type Discipline

Nullable reference types are enabled project-wide. Treat any new nullable warning your change introduces as a blocker, not a suggestion — resolve it at the source before considering the change complete.

```csharp
// Nullable properties: the domain genuinely allows absence
public ScopeId? ScopeId { get; set; }

// Non-nullable properties: absence is a bug, not a valid state
public string Content { get; set; } = string.Empty;
```

### WARNING: Suppressing Nullable Warnings with `!`

**The Problem:**

```csharp
// BAD - null-forgiving operator silences the compiler instead of fixing the issue
var document = await _documentRepository.GetByIdAsync(documentId, cancellationToken);
document!.UpdateContent(newContent); // compiler now trusts a lie
```

**Why This Breaks:**
1. The `!` operator doesn't make the value non-null — it just tells the compiler to stop checking, so a real null still throws `NullReferenceException` at runtime, just later and with less context.
2. It defeats the entire point of enabling nullable reference types for the project — every `!` is a hole in the safety net the rest of the team relies on.
3. Repository lookups like `IKnowledgeDocumentRepository.GetByIdAsync` are typed `Task<KnowledgeDocument?>` for a reason — a deleted/missing document legitimately comes back `null`, and swallowing that with `!` turns a recoverable not-found into an unhandled exception.

**The Fix:**

```csharp
// GOOD - handle the null case explicitly
var document = await _documentRepository.GetByIdAsync(documentId, cancellationToken);
if (document is null)
{
    return null;
}
document.UpdateContent(newContent);
```

**When You Might Be Tempted:** Right after a repository call you "know" will succeed because you just created the record. Even then, prefer returning the freshly-created aggregate from the creation method itself instead of re-fetching and asserting non-null.

## Value Objects Instead of Primitives

Domain identifiers are never raw `Guid` or `string` — they're `ValueObject`-derived types with private constructors and named factory methods.

```csharp
public sealed class ScopeId : ValueObject
{
    public Guid Value { get; }

    private ScopeId(Guid value) => Value = value;

    public static ScopeId New() => new(Guid.NewGuid());
    public static ScopeId From(Guid value) => new(value);

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Value;
    }
}
```

**DON'T** pass raw `Guid` between layers as if it were self-documenting:

```csharp
// BAD - which Guid is this? A document? A scope? Compiler can't tell you.
public async Task<KnowledgeDocumentDto?> GetAsync(Guid id, Guid otherId) { ... }
```

**Why This Breaks:** Primitive obsession lets you accidentally swap two `Guid` arguments of the same type and the compiler says nothing — you find out at runtime when the wrong document gets loaded. `DocumentId` and `ScopeId` are structurally distinct types even though both wrap a `Guid`, so a swapped argument is a compile error, not a production incident.

## Sealed Aggregates with Private Setters

Aggregate roots use `sealed class`, private property setters, a private (EF Core) constructor, and a public static factory:

```csharp
public sealed class KnowledgeDocument : AggregateRoot<DocumentId>
{
    public DocumentContent Content { get; private set; } = null!;
    public DateTime Timestamp { get; private set; }

    private KnowledgeDocument() { } // EF Core only

    public static KnowledgeDocument Create(ScopeId scopeId, DocumentContent content)
    {
        var document = new KnowledgeDocument { Id = DocumentId.New(), Content = content, Timestamp = DateTime.UtcNow };
        document.AddDomainEvent(new KnowledgeDocumentAddedDomainEvent(document.Id, scopeId));
        return document;
    }

    public void UpdateContent(DocumentContent newContent)
    {
        Content = newContent ?? throw new ArgumentNullException(nameof(newContent));
    }
}
```

**Why private setters matter:** a `public set` on `Content` means any code anywhere — a handler, a facade, a test double — can put the aggregate into an invalid state without going through `UpdateContent`'s validation. Private setters force every state change through a method that can enforce invariants and raise the corresponding domain event. This is what makes the aggregate "not anemic" — see the **ddd** skill for the full pattern.

## Dependency Injection via Constructors

```csharp
public sealed class KnowledgeContextFacade : IKnowledgeContextFacade
{
    private readonly IMediator _mediator;

    public KnowledgeContextFacade(IMediator mediator)
    {
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
    }
}
```

**DON'T** resolve dependencies via `IServiceProvider.GetRequiredService<T>()` inside a method body:

```csharp
// BAD - hides the real dependency graph, breaks constructor-based testability
public async Task<MyResult> DoSomethingAsync(IServiceProvider sp)
{
    var repo = sp.GetRequiredService<IMyRepository>();
    return await repo.GetAsync();
}
```

**Why This Breaks:** Constructor injection makes a class's dependencies visible at a glance and lets test code substitute mocks without touching a service locator. Service-locator-style resolution hides dependencies inside method bodies, which means a class can silently grow new dependencies without any test or reviewer noticing. See the **cqrs** skill for why the facade dispatches through `IMediator` instead.

## Anti-Patterns

### WARNING: Blocking on Async Code with `.Result` / `.Wait()`

**The Problem:**

```csharp
// BAD - blocks the calling thread until the Task completes
public IReadOnlyList<(KnowledgeDocument Document, double SimilarityScore)> SearchKnowledge(string searchText)
{
    var document = _documentRepository.GetByIdAsync(documentId).Result;
    return _vectorSearchService.SearchAsync(searchText, 5, 0.0, null, default).Result;
}
```

**Why This Breaks:**
1. On ASP.NET Core, blocking a request thread on `.Result` while it awaits another async operation can deadlock under the synchronization context in certain hosting scenarios, and always wastes a thread-pool thread that could serve another request.
2. Exceptions get wrapped in `AggregateException` instead of surfacing directly, so your `catch (Exception ex)` blocks stop matching the real exception type.
3. Under load, this is the single most common cause of thread-pool starvation in .NET web apps — request latency climbs non-linearly as concurrent requests increase.

**The Fix:**

```csharp
// GOOD - async all the way through the call stack
public async Task<IReadOnlyList<(KnowledgeDocument Document, double SimilarityScore)>> SearchKnowledgeAsync(string searchText, CancellationToken ct)
{
    var document = await _documentRepository.GetByIdAsync(documentId, ct);
    return await _vectorSearchService.SearchAsync(searchText, 5, 0.0, null, ct);
}
```

**When You Might Be Tempted:** Implementing a synchronous interface member that has to call an async repository method. The correct fix is to make the interface member async too — never sprinkle `.Result` to fit a sync signature.
