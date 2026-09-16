# PostgreSQL Patterns Reference

## Contents
- Schema Conventions
- pgvector Embeddings
- Apache AGE Graph Queries
- Value Object ID Mapping
- Anti-Patterns

---

## Schema Conventions

All tables and columns use snake_case, enforced via the EFCore.NamingConventions package. Configure
it once, in `AddKnowledgeContext`'s `UseNpgsql` chain — never manually rename a column.

```csharp
options.UseNpgsql(dataSource, npgsqlOptions => { /* ... */ })
       .UseSnakeCaseNamingConvention();   // PascalCase properties never leak into column names
```

**The one table this repository owns:**
- `knowledge.documents` — `id`, `scope_id`, `timestamp`, `vector` (`vector(1536)`), and the three
  columns the owned `DocumentContent` maps to: `content`, `source`, `category`

Everything relationship-shaped lives in the `knowledge_graph` Apache AGE graph, not in a relational
table — see *Apache AGE Graph Queries* below and the **apache-age** skill.

---

## pgvector Embeddings

`knowledge.documents.vector` stores 1536-dimension embeddings produced by whatever
`IEmbeddingGenerator` the host application registers. An HNSW index accelerates approximate
nearest-neighbor search.

```sql
-- Required extension — declared via HasPostgresExtension in KnowledgeDbContext, created by the
-- InitialCreate migration
CREATE EXTENSION IF NOT EXISTS vector;

-- HNSW index (created by the InitialCreate migration)
CREATE INDEX ix_documents_vector_hnsw ON knowledge.documents
    USING hnsw (vector vector_cosine_ops) WITH (m = 16, ef_construction = 64);
```

```csharp
// EF Core column mapping — Pgvector.EntityFrameworkCore package
entity.Property(e => e.Vector)
    .HasColumnType("vector(1536)");

// Querying: cosine distance (smaller = more similar)
var similar = await _context.Documents
    .Where(d => d.Embedding!.Vector.CosineDistance(queryVector) < maxDistance)
    .OrderBy(d => d.Embedding!.Vector.CosineDistance(queryVector))
    .Take(5)
    .ToListAsync(ct);
```

See the **pgvector** skill for the scope-filtered `MATERIALIZED` CTE this repository's repository
implementation actually uses, and why a plain filtered `ORDER BY ... LIMIT` under-fetches.

**WARNING:** Never call the embedding generator in a tight loop without batching. Each call is an
external round trip. `EmbeddingService` in `Dragonmind.Knowledge` handles this — use it, don't
inline embedding generation in a repository.

---

## Apache AGE Graph Queries

The Knowledge context stores entity relationships in Apache AGE. AGE requires `search_path` set
before running Cypher, and results come back as `agtype`, which this repository casts to `::text`
in the outer `SELECT` rather than relying on an extra AGE type-mapping plugin.

```csharp
// Must set search_path first — the AGE catalog is not in the default path
await using var cmd = connection.CreateCommand();
cmd.CommandText = "LOAD 'age'; SET search_path = ag_catalog, \"$user\", public;";
await cmd.ExecuteNonQueryAsync(ct);

var query = $@"
    SELECT subject::text, predicate::text, obj::text
    FROM cypher('knowledge_graph', $$
        MATCH (subject {{name: '{sanitizedName}'}})-[rel]->(obj)
        RETURN subject, type(rel) AS predicate, obj
    $$) AS (subject agtype, predicate agtype, obj agtype);
";
```

**Do NOT** concatenate unsanitized external input directly into Cypher strings — AGE's `cypher()`
function does not support parameter binding inside its literal body, so injection defense has to be
a sanitize-and-reject step before the string is ever built. See the **apache-age** skill for
`SanitizeCypher` and the full injection-defense chain.

---

## Value Object ID Mapping

Every aggregate uses a strongly-typed ID value object. EF Core needs a conversion to map it to and
from `uuid`.

```csharp
// KnowledgeDocumentEntityConfiguration
entity.Property(e => e.Id)
    .HasConversion(
        id => id.Value,          // DocumentId → Guid
        value => DocumentId.From(value));  // Guid → DocumentId

entity.Property(e => e.ScopeId)
    .HasConversion(
        id => id.Value,
        value => ScopeId.From(value))
    .HasColumnName("scope_id");
```

---

## Anti-Patterns

### WARNING: Running Migrations Without an Explicit Startup Project

**The Problem:**
```bash
# BAD — no --startup-project
dotnet ef migrations add Foo --project src/Dragonmind.Knowledge --context KnowledgeDbContext
```

**Why This Breaks:** With only two projects in this repository, `--project` and `--startup-project`
are almost always the same value, but omitting `--startup-project` still forces `dotnet ef` to guess
which project to build and run, which fails outside `src/Dragonmind.Knowledge` itself.

**The Fix:**
```bash
# GOOD — explicit on both
dotnet ef migrations add Foo \
    --project src/Dragonmind.Knowledge \
    --startup-project src/Dragonmind.Knowledge \
    --context KnowledgeDbContext
```

---

### WARNING: Missing pgvector/AGE Extensions

**The Problem:** Running migrations against a fresh PostgreSQL instance fails with
`type "vector" does not exist` or `extension "age" does not exist`.

**The Fix:** The `InitialCreate` migration creates both extensions as its first statements
(`CREATE EXTENSION IF NOT EXISTS vector;` / `age;`, the latter with `suppressTransaction: true`).
If you hit this error, you're most likely applying a later migration to a database that never ran
`InitialCreate` — run migrations from the start, not from an arbitrary point.

---

### WARNING: Bypassing the Repository With a Raw DbContext Injection

**The Problem:**
```csharp
// BAD — a handler injecting KnowledgeDbContext directly
public class SomeHandler(KnowledgeDbContext knowledgeCtx) { ... }
```

**Why This Breaks:** All data access is meant to flow through the repository interfaces in
`Domain/Repositories/`. Injecting the DbContext directly bypasses that seam, makes the caller
untestable without a real database, and couples it to EF Core specifics that the repository exists
to hide.

**The Fix:** Depend on `IKnowledgeDocumentRepository` / `IKnowledgeGraphRepository`, not
`KnowledgeDbContext`. See the **ddd** and **entity-framework** skills.
