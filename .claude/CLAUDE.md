# CLAUDE.md — AI Assistant Guide for Dragonmind Knowledge

This repository is the memory layer beneath a private multi-agent platform: scoped semantic search over pgvector plus a fact graph in Apache AGE, reached only through one anti-corruption-layer facade. The orchestration and agents that call this layer live elsewhere — there are no agents, prompts, or model calls in this codebase.

## Quick Facts

| Fact | Value |
|------|-------|
| **Tech Stack** | .NET 10, EF Core 10 + Npgsql, PostgreSQL 16 |
| **Storage** | pgvector (semantic search) + Apache AGE 1.6, same Postgres instance |
| **CQRS** | MediatR 12.5, one bounded context (`Dragonmind.Knowledge`) over one Foundation project (`Dragonmind.Core`) |
| **Tests** | xUnit v3 + Moq; unit tests mock everything, integration tests hit a real database |
| **Local DB** | docker compose, Postgres on `127.0.0.1:5455`, trust auth (local-only container) |
| **Scope isolation** | Every read that can cross scopes is parameterized by `ScopeId` — never a bare fetch |

## Task Completion Philosophy

Complete tasks fully rather than stopping at a "good stopping point." Implement full solutions, not partial ones with TODOs. If genuinely blocked, explain the technical blocker — not time constraints.

## Workflow Orchestration

**Plan First, Then Build** — plan any non-trivial task (3+ steps, or an architectural decision) with verification as explicit plan items; if something goes sideways, stop and re-plan rather than push down a broken path.

**Subagent Strategy** — one task per subagent, to keep the main context clean. Ad-hoc subagents always run Sonnet (pass `model: sonnet` explicitly; never let one silently inherit a higher-tier main session). Project agents (`.claude/agents/`) pin their model in frontmatter, Sonnet by default; never leave `model: inherit`.

**Verification Before Done** — never mark a task complete without proof: run the tests, check the build output, demonstrate correctness.

**Demand Elegance (Balanced)** — for non-trivial changes, ask "is there a more elegant way?"; if a fix feels hacky, do the clean version. Skip this for simple, obvious fixes.

**Autonomous Bug Fixing** — given a bug report, investigate the root cause, fix it, verify it; no routine context-switching back to the user.

**Follow-Up Work** — fix what you find in the current thread by default. A follow-up is only handled once it is fixed or the user has made an explicit decision about it; naming it as still open is not the same as handling it. Spawn separate work only when it is genuinely unrelated to what's in flight, or needs a decision only the user can make — ask for that decision in-thread, not by filing it away.

**Core Principles**: (1) *Simplicity First* — the simplest correct solution is the best one. (2) *No Laziness* — find root causes; no temporary patches. (3) *Minimal Impact* — touch only what the task requires; this governs how you implement, not a license to leave a discovered problem unhandled (see Follow-Up Work).

## Architecture Map

Two projects:
- **`Dragonmind.Core`** — Foundation. DDD base types (`Entity`, `ValueObject`, `DomainEvent`), the CQRS interfaces that bridge to MediatR (`Application/CQRS.cs`), the `IKnowledgeContextFacade` ACL interface and its DTOs, caching abstractions, and the `IEmbeddingGenerator` seam. Depends on nothing.
- **`Dragonmind.Knowledge`** — the bounded context. `KnowledgeDocument` and `KnowledgeFact` aggregates; commands (`AddKnowledgeDocument`, `CreateKnowledgeFact`) and queries (`SearchKnowledge`, `GetRelatedFacts`); the pgvector repository and the Apache AGE repository; `KnowledgeContextFacade` plus a caching decorator; `KnowledgeDbContext`.

Standard layout inside `Dragonmind.Knowledge`:
```
Domain/          Aggregates/ ValueObjects/ DomainEvents/ Repositories/ DomainServices/
Application/     Commands/{Name}/ Queries/{Name}/ DTOs/
Infrastructure/  Persistence/ Repositories/ AntiCorruptionLayer/ Caching/ DI/ Migrations/
```

**Dependency rules** (violations are bugs):
- Dependencies flow inward: Infrastructure → Application → Domain. Domain has zero infrastructure deps.
- `Dragonmind.Knowledge` depends on `Dragonmind.Core`; `Dragonmind.Core` depends on nothing.
- **Every consumer sees only `IKnowledgeContextFacade` and the DTOs it returns** — never a repository, an aggregate, or `KnowledgeDbContext` directly. That boundary is what this repo exists to demonstrate.
- **Every read that can cross scopes takes a `ScopeId`.** A read without one is a bug, not a shortcut: it is how one caller's data stays out of another's results.

## CQRS & DDD Rules (MUST follow)

1. **Commands** = state-changing; **Queries** = read-only. Sealed records with `required init` properties and `[Required]` on mandatory ones, implementing `ICommand<T>` / `IQuery<T>`.
2. **Handlers** implement `ICommandHandler<TCmd,TResult>` / `IQueryHandler<TQry,TResult>` with `HandleAsync`; the bridge in `Dragonmind.Core/Application/CQRS.cs` extends MediatR.
3. **NEVER register handlers individually in DI.** `AddKnowledgeContext`, the context's DI extension, registers MediatR with assembly scanning over `Dragonmind.Knowledge` — handlers are discovered, not registered. The DI extension registers repositories, domain services, and the facade only.
4. **Facades dispatch via `_mediator.Send()`** — never resolve a handler or repository from `IServiceProvider`.
5. **Aggregates**: sealed classes extending `AggregateRoot<TId>`, private setters, a private parameterless constructor for EF, a static `Create()` factory raising domain events. IDs are `ValueObject`s with `New()`/`From()` factories, mapped via `HasConversion`.
6. **Repository interfaces in Domain, implementations in Infrastructure.** Services never touch `KnowledgeDbContext` directly.
7. New operations: a command/handler pair in `Application/Commands/{Name}/` (or `Queries/`), no DI registration, a facade method only if reachable from outside this repo.

## Code Conventions

| Type | Convention | Example |
|------|-----------|---------|
| Classes / Methods / Properties | PascalCase | `KnowledgeContextFacade`, `SearchKnowledgeAsync` |
| Private fields | _camelCase | `_documentRepository` |
| Interfaces | IPascalCase | `IKnowledgeGraphRepository` |
| Parameters / locals | camelCase | `scopeId` |
| DB tables / columns | snake_case | `knowledge.documents`, `scope_id` |

- **Always async/await for I/O** — never `.Result`/`.Wait()`.
- **Constructor injection with interfaces**; register services in the context's DI extension.
- **Nullable reference types enabled everywhere** — use `string?` or initialize; null-check injected deps.
- Let exceptions propagate from handlers; a repository never swallows one.
- **Global usings**: each project has a `GlobalUsings.cs` — match it, don't repeat usings.
- **XML doc comments** on interfaces and public methods.

## Commands

```bash
# Build
dotnet build Dragonmind.Knowledge.sln

# Unit tests (no database required)
dotnet test tests/Dragonmind.Knowledge.UnitTests

# Integration tests (real Postgres, pgvector + Apache AGE)
docker compose up -d --wait db
dotnet test tests/Dragonmind.Knowledge.IntegrationTests

# Everything in one command
docker compose --profile test up --build --exit-code-from tests

# Agents' knowledge-base MCP container (see Knowledge Base, below)
docker compose --profile agents up -d

# EF Core migrations
dotnet ef migrations add <Name> --project src/Dragonmind.Knowledge --startup-project src/Dragonmind.Knowledge --context KnowledgeDbContext
```

Env vars: `KNOWLEDGE_TEST_CONNECTION` for the integration tests, `KNOWLEDGE_DB_CONNECTION` for `dotnet ef`.

## Code Review Checklist

**Architecture**: correct project (`Core` vs `Knowledge`); CQRS (commands write, queries read); handlers not registered in DI; consumers reach this context only through `IKnowledgeContextFacade`; Domain has zero infra deps; aggregates encapsulate logic (not anemic); value objects over primitives; domain events for significant changes.

**Code quality**: async/await for I/O; null safety; services registered in the DI extension; tests added; XML docs; every cross-scope read takes a `ScopeId`; DB changes carry a migration; no circular deps; naming conventions.

**CQRS specifics**: immutable records with `init`; `[Required]` on mandatory command properties; constructor injection; facade methods use `_mediator.Send()`; DTOs cross the boundary, never aggregates; repository interfaces live in Domain.

## Skill Usage Guide

Invoke these unprefixed when working on the matching technology:

| Skill | Invoke When |
|-------|-------------|
| csharp | C# 13 syntax, nullable reference types, language conventions |
| dotnet | .NET 10 runtime, project and solution structure |
| cqrs | Command and query handlers dispatched via MediatR |
| ddd | Aggregates, value objects, bounded-context boundaries |
| entity-framework | EF Core 10 `DbContext`, migrations, entity mappings |
| postgresql | PostgreSQL 16 schemas, connections, queries |
| pgvector | Embeddings, HNSW indexes, similarity search |
| apache-age | Cypher queries, graph traversal, fact entities |
| moq | Mock objects for repository and dependency-injection tests |
| xunit | xUnit v3 tests (`Fact`/`Theory`), Arrange-Act-Assert |
| coverlet | Code coverage via coverlet instrumentation |
| docker | Docker containers and the docker-compose services |
| mcp | The `.mcp.json` wiring and the agents' knowledge-base container |
| git | Version control, branching, and commit conventions |

**Plugin & External Skill Guidance**: the skills and agents in this repo are project-owned and always win over a plugin or runtime skill/agent with the same name or purpose — never load a generically-named stand-in when a project skill covers the same ground. Never scaffold with a generic template: this repo's structure (two `src` projects, two `tests` projects, one solution) is intentional and small; build within it rather than regenerating it.

## Knowledge Base (on-demand reference)

A KnowledgeBase MCP server backs this repo (`mcp__knowledgebase__*` tools). It is the third-party [mbcrawfo/KnowledgeBaseServer](https://github.com/mbcrawfo/KnowledgeBaseServer) (MIT), wired unchanged in `.mcp.json` and reached over `docker exec` into a container named `knowledgebase-mcp`. Start it with `docker compose --profile agents up -d`.

**Search before you start work, and search again before you write** — a topic that already covers the fact should be corrected or extended, never duplicated.

**Topics** (write only to these five):

| Topic | Contains |
|-------|----------|
| `architecture` | Layout, aggregates, commands/queries, facade shape, repository quirks |
| `database` | Schema/column semantics, migration notes |
| `troubleshooting` | Root cause and fix for a non-obvious failure, keyed by the exact exception type |
| `how-to` | Repeatable procedures that took more than one attempt to get right |
| `conventions` | Standing decisions and the reasoning behind them |

`CreateMemory` silently creates a topic it doesn't recognize, so a typo becomes a permanent orphan — call `GetTopics` first if unsure.

**Search is FTS5 with porter stemming — whole tokens, not substrings.** `migration` finds `migrations`, but `Repository` will not find a partial identifier like `EfCoreKnowledgeDocumentRepository`. Use complete literal tokens (class names, exception types, table names) and pass several phrases — they OR together.

**Importance**: 0.9–1.0 footguns causing data loss or silent corruption; 0.7–0.8 contracts and decisions that constrain future work; 0.5 default reference detail; 0.3 narrow or nice-to-know.

**Correcting a memory.** There is no built-in way to mark one outdated, so a correction is additive: create the corrected memory with its text opening `Correction:` and stating what it replaces, then `ConnectMemories` from the older memory's id to the new one. Never leave a contradicting memory unlinked.
