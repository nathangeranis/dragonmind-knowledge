# DDD Workflows Reference

## Contents
- Adding a New Aggregate
- Wiring a New Consumer Through the Facade
- Verification Loop

## Adding a New Aggregate

1. **Confirm the concept belongs in this context** — ask "does this belong to the Knowledge domain's ubiquitous language (documents, facts, relationships, embeddings)?" If it's a different concern entirely, it likely belongs in a separate bounded context, not bolted onto this one.

2. **Create the aggregate root** in `Domain/Aggregates/`:

```csharp
public sealed class MyNewAggregate : AggregateRoot<MyNewAggregateId>
{
    public string Name { get; private set; } = string.Empty;
    private MyNewAggregate() { }

    public static MyNewAggregate Create(string name)
    {
        var aggregate = new MyNewAggregate { Id = MyNewAggregateId.New(), Name = name };
        aggregate.AddDomainEvent(new MyNewAggregateCreatedDomainEvent(aggregate.Id));
        return aggregate;
    }
}
```

3. **Create the ID value object** in `Domain/ValueObjects/` (see `references/patterns.md`).

4. **Create the repository interface** in `Domain/Repositories/`:

```csharp
public interface IMyNewAggregateRepository
{
    Task<MyNewAggregate?> GetByIdAsync(MyNewAggregateId id, CancellationToken ct = default);
    Task AddAsync(MyNewAggregate aggregate, CancellationToken ct = default);
}
```

5. **Implement the repository** in `Infrastructure/Repositories/` with EF Core — see the **entity-framework** skill for `HasConversion` on the ID value object and JSONB mapping.

6. **Add the DbSet and migration:**

```bash
dotnet ef migrations add AddMyNewAggregate \
    --project src/Dragonmind.Knowledge \
    --startup-project src/Dragonmind.Knowledge \
    --context KnowledgeDbContext
```

7. **Add CQRS commands/queries** to expose the aggregate — see the **cqrs** skill.

## Wiring a New Consumer Through the Facade

**When:** Code outside this repository needs a fact or document that only `Dragonmind.Knowledge` owns.

1. Check whether `IKnowledgeContextFacade` already exposes the needed read/write. If yes, inject it directly.
2. If not, add a method to the facade interface in `Dragonmind.Core/Application/AntiCorruptionLayer/`, implement it in `Dragonmind.Knowledge/Infrastructure/AntiCorruptionLayer/`, backed by a new query/command dispatched via `_mediator.Send()`.
3. Inject the facade — never the repository — into the consuming code:

```csharp
public sealed class KnowledgeLookupService
{
    private readonly IKnowledgeContextFacade _knowledgeContext;

    public KnowledgeLookupService(IKnowledgeContextFacade knowledgeContext)
    {
        _knowledgeContext = knowledgeContext;
    }

    public async Task<IReadOnlyList<KnowledgeSnippetDto>> LookUpAsync(string topic, CancellationToken ct)
    {
        return await _knowledgeContext.SearchKnowledgeAsync(topic, scopeId: null, maxResults: 5, ct);
    }
}
```

4. Register the context in the host's DI container with `services.AddKnowledgeContext(dataSource, options => { ... })` — this single extension method registers the MediatR assembly scan, the pipeline behaviors, the DbContext factory, the repositories, and the facade. No separate per-handler registration is needed (see the **cqrs** skill).

## Verification Loop

DDD boundary violations are structural, not just logical — verify them explicitly rather than trusting a read-through:

1. Make the change (new aggregate, new facade method).
2. Validate: `dotnet build Dragonmind.Knowledge.sln` — confirms `Dragonmind.Core` still has zero references to `Dragonmind.Knowledge` or any EF Core/Npgsql type.
3. Run the domain tests: `dotnet test tests/Dragonmind.Knowledge.UnitTests`
4. Run `AclBoundaryTests` specifically if you touched the facade contract: `dotnet test --filter "FullyQualifiedName~AclBoundaryTests"`
5. Grep for domain purity: search `Dragonmind.Knowledge/Domain/` for `using Microsoft.EntityFrameworkCore` — any hit is a violation (Domain has zero infrastructure dependencies).
6. If validation fails, fix the boundary violation (route through the facade) and repeat step 2.
7. Only mark the task complete once build, tests, and the boundary checks all pass.
