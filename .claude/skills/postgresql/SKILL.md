---
name: postgresql
description: |
  Manages the PostgreSQL 16 schema, migrations, and queries for the Knowledge bounded context.
  Use when: creating EF Core migrations, writing pgvector similarity searches, querying the Apache
  AGE knowledge graph, mapping value-object columns, troubleshooting local connection issues, or
  working with the snake_case schema conventions enforced by EF Core naming conventions.
allowed-tools: Read, Edit, Write, Glob, Grep, Bash
---

# PostgreSQL Skill

This repository uses PostgreSQL 16 (in Docker, via `docker compose`) with two extensions loaded into
one database: **pgvector** for 1536-dimension semantic embeddings (`knowledge.documents.vector`) and
**Apache AGE** for graph traversal via Cypher (`knowledge_graph`). The Knowledge context owns one EF
Core `KnowledgeDbContext` and one `Migrations/` folder. All tables and columns are snake_case via
`UseSnakeCaseNamingConvention()`. The local database runs on `127.0.0.1:5455` (configurable via
`KNOWLEDGE_DB_PORT`) with trust authentication — there is no password to configure locally.

## Quick Start

### Connection Strings

```
# Local compose database (docker compose up -d --wait db)
Host=127.0.0.1;Port=5455;Database=knowledge;Username=knowledge

# Inside the tests container, on the compose network (KNOWLEDGE_TEST_CONNECTION)
Host=db;Port=5432;Database=knowledge;Username=knowledge
```

### Migration

```bash
dotnet ef migrations add AddDocumentIndex \
    --project src/Dragonmind.Knowledge \
    --startup-project src/Dragonmind.Knowledge \
    --context KnowledgeDbContext
```

`KNOWLEDGE_DB_CONNECTION` must be set in the environment — `KnowledgeDbContextFactory` (the
design-time factory `dotnet ef` uses) reads it and throws if it's unset, rather than falling back to
a guessed connection string.

### pgvector Similarity Search

```csharp
// Cosine distance on 1536-dimension embeddings — HNSW index on knowledge.documents.vector
var results = await _context.Documents
    .OrderBy(d => d.Embedding!.Vector.CosineDistance(queryVector))
    .Take(maxResults)
    .ToListAsync(ct);
```

(The repository actually runs this as raw SQL for the scope-filtered `MATERIALIZED` CTE case — see
the **pgvector** skill.)

## Key Concepts

| Concept | Detail | Where |
|---------|--------|-------|
| Extensions | `vector`, `age` | Created by the `InitialCreate` migration's `Up()`, not by a separate init script |
| Naming | snake_case tables/columns | `UseSnakeCaseNamingConvention()` in `AddKnowledgeContext` and the design-time factory |
| Migrations | One context, one `Migrations/` folder | `src/Dragonmind.Knowledge/Migrations/` |
| Embeddings | `vector(1536)` column type | `knowledge.documents.vector` |
| Graph | `knowledge_graph` | Created by the same migration, guarded by an existence check |
| Local port | `127.0.0.1:5455` (`KNOWLEDGE_DB_PORT`) | `docker-compose.yml`, trust auth, no password |

## Common Patterns

### Scope Id Column (EF Core)

```csharp
entity.Property(e => e.ScopeId)
    .HasConversion(
        id => id.Value,
        value => ScopeId.From(value))
    .HasColumnName("scope_id");
```

### Value Object ID Conversion

```csharp
entity.Property(e => e.Id)
    .HasConversion(
        id => id.Value,
        value => DocumentId.From(value));
```

## See Also

- [patterns](references/patterns.md) — Schema conventions, pgvector, Apache AGE, anti-patterns
- [workflows](references/workflows.md) — Migration workflow, local Docker setup, troubleshooting

## Related Skills

- See the **entity-framework** skill for DbContext configuration and EF Core migration details
- See the **ddd** skill for how the Knowledge context owns its own DbContext
- See the **docker** skill for the local compose database
- See the **dotnet** skill for `dotnet ef` CLI usage
