# DDD Patterns Reference

## Contents
- Aggregate Design
- Value Objects
- Domain Events
- The Anti-Corruption-Layer Boundary
- Anti-Patterns to Avoid

## Aggregate Design

Aggregates are the only unit of persistence and consistency. Every aggregate extends `AggregateRoot<TId>` from `Dragonmind.Core/Domain/` and is constructed through a private constructor + static factory, never a public constructor:

```csharp
public sealed class KnowledgeFact : AggregateRoot<FactId>
{
    public GraphEntity Subject { get; private set; } = null!;
    public string Predicate { get; private set; } = string.Empty;
    public GraphEntity Object { get; private set; } = null!;

    private KnowledgeFact() { } // required by EF Core

    public static KnowledgeFact Create(ScopeId scopeId, GraphEntity subject, string predicate, GraphEntity @object)
    {
        var fact = new KnowledgeFact
        {
            Id = FactId.New(),
            Subject = subject,
            Predicate = predicate,
            Object = @object
        };
        fact.AddDomainEvent(new KnowledgeFactCreatedDomainEvent(fact.Id, scopeId));
        return fact;
    }

    public void UpdateObject(GraphEntity newObject)
    {
        ArgumentNullException.ThrowIfNull(newObject);

        Object = newObject;
        AddDomainEvent(new KnowledgeFactUpdatedDomainEvent(Id));
    }
}
```

**WARNING: Anemic aggregates (public setters, no behavior)**

```csharp
// BAD - anemic model, any caller can put the aggregate in an invalid state
public sealed class KnowledgeFact : AggregateRoot<FactId>
{
    public GraphEntity Object { get; set; } = null!;
}
```

**Why This Breaks:**
1. Invariants live nowhere — validation gets duplicated (or skipped) in every handler that touches the aggregate.
2. Business rules scatter across command handlers instead of living with the data they govern, so DDD's core value — a single place to reason about correctness — is lost.
3. A future refactor that adds a new invariant (e.g., "the predicate must be one of the allowed relationship types") has no natural home and gets bolted onto call sites inconsistently.

**The Fix:** Private setters, behavior methods (`UpdateObject`), factory methods for creation. See `KnowledgeFact` above.

## Value Objects

Value objects have no identity — two instances with the same values are equal. Use them for domain concepts, not primitives:

```csharp
public sealed class Embedding : ValueObject
{
    public float[] Vector { get; }

    private Embedding(float[] vector) => Vector = vector;

    public static Embedding Create(float[] vector)
    {
        if (vector.Length != EmbeddingDimensions.Default)
            throw new ArgumentException($"Expected {EmbeddingDimensions.Default} dimensions, got {vector.Length}");
        return new Embedding((float[])vector.Clone());
    }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        foreach (var v in Vector) yield return v;
    }
}
```

**WARNING: Primitive obsession instead of value objects**

```csharp
// BAD - a loose float[], nothing stops a wrong-dimension array reaching the vector column
public void SetEmbedding(float[] values) { ... }
```

**Why This Breaks:** The compiler can't catch a caller passing a 768-dimension array where the pgvector column expects 1536 — that fails at the database, not at the call site. A value object that validates its invariant in the constructor makes that class of bug impossible to construct in the first place.

**The Fix:** Wrap related primitives in a `ValueObject`-derived type as shown above.

## Domain Events

Raise domain events for anything another part of the system — inside or outside the context — might need to react to:

```csharp
public sealed record KnowledgeDocumentAddedDomainEvent(DocumentId DocumentId, ScopeId? ScopeId) : DomainEvent;
```

Domain events are raised inside the aggregate (`AddDomainEvent(...)`), not by the handler after the fact — this keeps the "what happened" close to the invariant that caused it, and ensures the event fires even if the aggregate method is called from an untested code path.

## The Anti-Corruption-Layer Boundary

`Dragonmind.Knowledge/Domain/` has **zero infrastructure dependencies** and is never referenced directly by anything outside this repository. The only supported entry point is `IKnowledgeContextFacade`, declared in `Dragonmind.Core/Application/AntiCorruptionLayer/` and implemented in `Dragonmind.Knowledge/Infrastructure/AntiCorruptionLayer/`.

**WARNING: Reaching past the facade**

```csharp
// BAD - a consumer holding a reference to the repository interface directly
public class ConsumingService
{
    private readonly IKnowledgeDocumentRepository _documentRepo; // Wrong layer to depend on!
}
```

**Why This Breaks:**
1. Couples the consumer's lifecycle to the Knowledge context's persistence schema — a migration in `Dragonmind.Knowledge` can now break the consumer at compile time or, worse, at runtime.
2. Bypasses the aggregate's own invariants (a repository has no business logic; the aggregate/handler does).
3. Defeats the entire point of the facade: independent evolution of the storage layer underneath it.

**The Fix:** Inject `IKnowledgeContextFacade` and call its public methods. See `references/workflows.md` for the exact wiring, and the **cqrs** skill for how the facade dispatches commands/queries. `AclBoundaryTests` (`tests/Dragonmind.Knowledge.UnitTests`) enforces this with reflection: it fails the build if `Dragonmind.Core` ever references the Knowledge assembly, or if the facade contract leaks an EF Core / Npgsql / Pgvector type.

## Anti-Patterns to Avoid

| Anti-Pattern | Problem | Fix |
|--------------|---------|-----|
| Public setters on aggregates | Invariants bypassed | Private setters + behavior methods |
| A consumer depending on the repository interface instead of the facade | Couples the consumer to the storage schema | Depend only on `IKnowledgeContextFacade` and its DTOs |
| Domain layer referencing EF Core types | Domain becomes untestable without a DB | Domain has zero infrastructure references |
| Raising domain events from the handler instead of the aggregate | Event can be forgotten on other code paths | Raise inside aggregate methods |
