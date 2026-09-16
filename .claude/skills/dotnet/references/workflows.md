# .NET Build & Test Workflows Reference

## Contents
- Full Solution Build-Verify Loop
- Test Workflow
- Adding a New Aggregate or Repository (Checklist)
- Clean Rebuild When Things Look Wrong
- CI-Equivalent Local Verification

## Full Solution Build-Verify Loop

Standard iterate-until-pass loop for any change spanning more than one file:

1. Make changes
2. Validate: `dotnet build Dragonmind.Knowledge.sln`
3. If build fails, fix issues and repeat step 2
4. Only proceed to testing once the build is clean

```bash
dotnet build Dragonmind.Knowledge.sln
```

If you want analyzer warnings to fail the build the same way CI would (recommended before opening a PR):

```bash
dotnet build Dragonmind.Knowledge.sln -warnaserror
```

### WARNING: Treating "it compiled" as "it works"

**The Problem:** stopping after `dotnet build` succeeds and reporting the task done.

**Why This Breaks:** a clean build only proves the code is syntactically and type-correct — it says nothing about whether a handler actually persists correctly, or whether the facade still dispatches through `IMediator.Send()` instead of resolving a repository directly (a CQRS violation `AclBoundaryTests` and code review both check for). Build-only verification lets real regressions through.

**The Fix:** always follow a build with the relevant `dotnet test` invocation (see below) before calling anything done.

## Test Workflow

Run the unit tests for fast, database-free feedback while iterating:

```bash
dotnet test tests/Dragonmind.Knowledge.UnitTests
```

Run a single test class when iterating quickly on one failure:

```bash
dotnet test --filter "FullyQualifiedName~KnowledgeDocumentTests"
```

Run the integration tests (real PostgreSQL, pgvector, and Apache AGE via Docker Compose) before finishing a branch, or whenever you touch a repository, the DbContext, or a migration:

```bash
docker compose up -d --wait db
dotnet test tests/Dragonmind.Knowledge.IntegrationTests
```

Or run everything, including the containerized database, in one command:

```bash
docker compose --profile test up --build --exit-code-from tests
```

### WARNING: Skipping tests because "it's just a small change"

A change to `Dragonmind.Core/Application/CQRS.cs` (the MediatR bridge) or `AggregateRoot<T>` affects everything built on top of it. Foundation-layer changes always warrant the full solution test run, not just the unit project you were already touching.

## Adding a New Aggregate or Repository (Checklist)

Copy this checklist and track progress (see the **ddd** and **cqrs** skills for the Domain/Application/Infrastructure content that goes inside):

- [ ] Add the aggregate in `src/Dragonmind.Knowledge/Domain/Aggregates/`
- [ ] Add the repository interface in `Domain/Repositories/`, implementation in `Infrastructure/Repositories/`
- [ ] Add the `DbSet` and entity configuration to `KnowledgeDbContext`
- [ ] Create a migration: `dotnet ef migrations add Add{Aggregate} --project src/Dragonmind.Knowledge --startup-project src/Dragonmind.Knowledge --context KnowledgeDbContext`
- [ ] Register the repository in `AddKnowledgeContext()` (`Infrastructure/DI/KnowledgeContextExtension.cs`) — not the handlers, MediatR discovers those via assembly scanning
- [ ] Add unit tests to `tests/Dragonmind.Knowledge.UnitTests`
- [ ] Add an integration test to `tests/Dragonmind.Knowledge.IntegrationTests` if the repository does anything a mock can't exercise (raw SQL, vector search, Cypher)
- [ ] `dotnet build Dragonmind.Knowledge.sln` — confirm clean build
- [ ] `dotnet test tests/Dragonmind.Knowledge.UnitTests` — confirm green

## Clean Rebuild When Things Look Wrong

When build errors seem to reference stale state (a renamed type still "found" elsewhere, a deleted file still resolving):

```bash
dotnet clean
dotnet build Dragonmind.Knowledge.sln
```

## CI-Equivalent Local Verification

Before opening a PR, run the same sequence CI runs — don't rely on `dotnet build` alone having been "close enough":

```bash
dotnet clean
dotnet build Dragonmind.Knowledge.sln -warnaserror
docker compose up -d --wait db
dotnet test Dragonmind.Knowledge.sln --logger "console;verbosity=detailed"
docker compose down -v
```

For code-coverage-gated verification, see the **coverlet** skill; for the migration-specific `dotnet ef` commands referenced above, see the **entity-framework** skill.
