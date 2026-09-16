---
name: entity-framework
description: |
  Configures EF Core 10.0 for the Knowledge bounded context: DbContext, migrations, value-object
  mapping, and the pgvector/Apache AGE integration points.
  Use when: adding an entity or aggregate, creating a migration, configuring KnowledgeDbContext,
  mapping value objects, or troubleshooting migration issues.
allowed-tools: Read, Edit, Write, Glob, Grep, Bash
---

# Entity Framework Skill

This repository uses EF Core 10.0 with a **single DbContext**, `KnowledgeDbContext`, owning the
`knowledge` schema and its own migration history. All tables use snake_case. Direct `DbContext`
access outside the repository implementations is forbidden — all data access flows through
`Domain/Repositories/` interfaces.

## Quick Start

### DbContext shape

```csharp
// src/Dragonmind.Knowledge/Infrastructure/Persistence/KnowledgeDbContext.cs
public sealed class KnowledgeDbContext : DbContext, IUnitOfWork
{
    public KnowledgeDbContext(DbContextOptions<KnowledgeDbContext> options)
        : base(options) { }

    public DbSet<KnowledgeDocument> Documents => Set<KnowledgeDocument>();

    public async Task<int> CommitAsync(CancellationToken cancellationToken = default)
        => await SaveChangesAsync(cancellationToken);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasPostgresExtension("vector");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(KnowledgeDbContext).Assembly);
        modelBuilder.HasDefaultSchema("knowledge");
    }
}
```

### Owned value object + strongly-typed ID (required for every aggregate)

```csharp
// KnowledgeDocumentEntityConfiguration.cs
modelBuilder.Entity<KnowledgeDocument>(entity =>
{
    entity.ToTable("documents");   // → knowledge.documents
    entity.HasKey(e => e.Id);
    entity.Property(e => e.Id)
        .HasConversion(id => id.Value, value => DocumentId.From(value));
    entity.Property(e => e.ScopeId)
        .HasConversion(id => id.Value, value => ScopeId.From(value))
        .HasColumnName("scope_id");

    entity.OwnsOne(e => e.Embedding, embedding =>
    {
        embedding.Property(e => e.Vector)
            .HasColumnName("vector")
            .HasColumnType($"vector({EmbeddingDimensions.Default})")
            .HasConversion(v => new Pgvector.Vector(v), v => v.ToArray());
    });
});
```

### Create a migration

```bash
dotnet ef migrations add AddDocumentContentIndex \
    --project src/Dragonmind.Knowledge \
    --startup-project src/Dragonmind.Knowledge \
    --context KnowledgeDbContext

dotnet ef database update \
    --project src/Dragonmind.Knowledge \
    --startup-project src/Dragonmind.Knowledge \
    --context KnowledgeDbContext
```

## Key Concepts

| Concept | Usage | Notes |
|---------|-------|-------|
| Single DbContext | `KnowledgeDbContext` | No other context to keep separate from — but the pattern (own schema, own migration history table) still matters if this repository ever grows a second one |
| snake_case | All table/column names | Via `.UseSnakeCaseNamingConvention()` in `AddKnowledgeContext` and the design-time factory |
| `HasPostgresExtension("vector")` | Declares the extension on the EF model | Complements, doesn't replace, `CREATE EXTENSION` in the migration itself |
| Value object conversion | Every ID and the embedding vector | Required for every aggregate |
| `ValueComparer` | Needed for the `float[]`-backed vector property | EF's default reference-equality change tracking cannot see an in-place array mutation — see [patterns](references/patterns.md) |
| Repository pattern | All data access | NEVER inject `KnowledgeDbContext` into a handler or service directly |
| Pooled factory | `IDbContextFactory<KnowledgeDbContext>` | Used instead of a scoped `DbContext` directly, because pgvector/AGE are wired at the `NpgsqlDataSource` level — see the **pgvector** and **apache-age** skills |

## See Also

- [patterns](references/patterns.md) — entity configuration, value objects, the vector `ValueComparer`
- [workflows](references/workflows.md) — migrations, rollback, common errors

## Related Skills

- See the **postgresql** skill for connection strings and extension/graph setup
- See the **ddd** skill for aggregate and value object patterns
- See the **cqrs** skill for repository interfaces and handler registration
- See the **dotnet** skill for project structure and DI registration
