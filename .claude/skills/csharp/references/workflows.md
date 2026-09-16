# C# Workflows Reference

## Contents
- Adding a New Command/Handler Pair
- Verifying Nullable Safety on New Code
- Build-and-Fix Iteration Loop
- Renaming/Refactoring a Value Object

## Adding a New Command/Handler Pair

This is the most common C#-authoring workflow in this codebase: every state-changing operation follows the same shape.

Copy this checklist and track progress:
- [ ] Create the command record in `Dragonmind.Knowledge/Application/Commands/{Name}/{Name}Command.cs`
- [ ] Mark mandatory properties `required` with `[Required]` where validation matters
- [ ] Create the handler in the same directory implementing `ICommandHandler<TCommand, TResult>`
- [ ] Confirm nullable annotations on the handler's return type match what callers actually receive (`MyDto?` vs `MyDto`)
- [ ] Do **not** register the handler in DI — MediatR discovers it via assembly scanning
- [ ] Update `KnowledgeContextFacade` to dispatch via `_mediator.Send(new MyCommand { ... })` if the operation needs to cross the ACL boundary
- [ ] Add a unit test in `tests/Dragonmind.Knowledge.UnitTests`

```csharp
public sealed record MyCommand : ICommand<MyResult>
{
    [Required]
    public required string Name { get; init; }
    public int Value { get; init; }
}

public sealed class MyCommandHandler : ICommandHandler<MyCommand, MyResult>
{
    private readonly IMyRepository _repository;

    public MyCommandHandler(IMyRepository repository)
    {
        _repository = repository;
    }

    public async Task<MyResult> HandleAsync(MyCommand command, CancellationToken cancellationToken = default)
    {
        // 1. Validate, 2. Create/update aggregate, 3. Persist, 4. Return result
        var aggregate = MyAggregate.Create(command.Name);
        await _repository.AddAsync(aggregate, cancellationToken);
        return new MyResult(aggregate.Id.Value);
    }
}
```

For the MediatR dispatch bridge and facade wiring, see the **cqrs** skill; for the aggregate itself, see the **ddd** skill.

## Verifying Nullable Safety on New Code

Every new C# file must compile clean under nullable reference types — the solution has `<Nullable>enable</Nullable>` set solution-wide, so a new nullable warning is a build health regression, not a style nit.

1. Write the code, marking each reference type explicitly nullable (`?`) or leaving it non-nullable per its actual domain meaning.
2. Validate: `dotnet build Dragonmind.Knowledge.sln` and inspect the warning output — your new or changed files must introduce zero `CS860x` nullable warnings (don't gate on the whole solution being warning-free; pre-existing warnings elsewhere are not your worklist).
3. Resolve each new nullable warning at its source — add a null check, change the property to `?`, or fix a factory method that isn't actually guaranteeing non-null. Never suppress with `!` as a substitute (see `references/patterns.md`).
4. Re-run the build. Only proceed once your files are clean.

```bash
dotnet build Dragonmind.Knowledge.sln
```

## Build-and-Fix Iteration Loop

For any non-trivial change touching multiple files (e.g. renaming a shared value object, adding a required property to a widely-used record), don't guess which call sites need updates — let the compiler enumerate them.

1. Make the change in the source-of-truth file (e.g. the value object or record definition).
2. Validate: `dotnet build Dragonmind.Knowledge.sln`
3. If the build fails, the errors are your worklist — fix each reported call site, don't skip ahead.
4. Re-run `dotnet build Dragonmind.Knowledge.sln` after each batch of fixes.
5. Repeat steps 3-4 until the build is clean.
6. Only then run the affected test project: `dotnet test tests/Dragonmind.Knowledge.UnitTests`

This loop is faster and more reliable than grepping for usages by hand, because the compiler can't miss a call site the way a text search can (e.g. missing a usage behind an interface or generic constraint).

## Renaming/Refactoring a Value Object

**When:** A domain concept's `ValueObject` (e.g. `ScopeId`) needs a new factory method, an added invariant, or a renamed property.

1. Change the `ValueObject` definition first — this is the single source of truth.
2. Run the Build-and-Fix Iteration Loop above to catch every call site.
3. Check `GetEqualityComponents()` still yields every field that should participate in equality — a forgotten field here silently breaks value equality (two logically-different instances compare equal).
4. Update EF Core's `HasConversion` mapping in `KnowledgeDbContext`'s entity configuration if the value object's underlying representation changed — see the **entity-framework** skill.
5. Re-run domain unit tests to confirm equality and validation behavior didn't regress.

**DON'T** change a value object's equality components without re-running its domain tests — a value object's entire purpose is correct equality semantics, and that's exactly the kind of regression that compiles cleanly but breaks at runtime (e.g. two aggregates with "equal" IDs now compare unequal, breaking dictionary lookups or `Distinct()` calls downstream).
