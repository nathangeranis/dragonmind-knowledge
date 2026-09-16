---
name: pgvector
description: |
  Handles pgvector embeddings for semantic search in the Knowledge bounded context.
  Use when: configuring pgvector column mappings in EF Core, writing similarity search queries,
  generating embeddings via IEmbeddingGenerator, troubleshooting vector storage or search issues,
  or optimizing with HNSW indexes.
allowed-tools: Read, Edit, Write, Glob, Grep, Bash
---

# pgvector Skill

This repository uses **pgvector** for semantic similarity search in the Knowledge bounded context.
Embeddings are produced by an injected `IEmbeddingGenerator` (a `Dragonmind.Core` abstraction over
`Microsoft.Extensions.AI` — no concrete provider ships in this repo) at a fixed, enforced dimension
(`EmbeddingDimensions.Default = 1536`) and stored in `knowledge.documents.vector`. Packages:
`Pgvector 0.3.2` + `Pgvector.EntityFrameworkCore 0.3.0` (in `Dragonmind.Knowledge.csproj`).

## Quick Start

### Store and Search via Facade

```csharp
// Store knowledge with auto-embedding
await _knowledgeContext.StoreKnowledgeAsync(
    content: "The Checkout Service calls Payments DB for every order.",
    source: "architecture-notes",
    scopeId: scopeId);

// Semantic search, scoped to one caller
var results = await _knowledgeContext.SearchKnowledgeAsync(
    query: "which service talks to the payments database",
    scopeId: scopeId,
    maxResults: 5);
```

### EF Core Column Mapping (Critical)

```csharp
// BOTH UseVector() AND HasColumnType are required
builder.OwnsOne(e => e.Embedding, embedding =>
{
    embedding.Property(e => e.Vector)
        .HasColumnName("vector")
        .HasColumnType($"vector({EmbeddingDimensions.Default})")
        .HasConversion(
            v => new Pgvector.Vector(v),    // float[] -> pgvector
            v => v.ToArray());              // pgvector -> float[]
});
```

`UseVector()` must be wired in **two layers** (missing either breaks vector mapping):

```csharp
// 1) KnowledgeDataSource.Create(connectionString) — before the NpgsqlDataSource is handed
//    to AddKnowledgeContext
dataSourceBuilder.UseVector().Build();

// 2) EF Core options — inside AddKnowledgeContext
//    (also mirrored in KnowledgeDbContextFactory for design-time migrations)
options.UseNpgsql(dataSource, npgsqlOptions =>
{
    npgsqlOptions.UseVector();
});
```

## Key Concepts

| Concept | Value | Notes |
|---------|-------|-------|
| Embedding seam | `IEmbeddingGenerator` (`Dragonmind.Core`) | No concrete provider ships here — `ExtensionsAIEmbeddingGenerator` wraps whatever `Microsoft.Extensions.AI.IEmbeddingGenerator<string, Embedding<float>>` the host application registers |
| Dimensions | `EmbeddingDimensions.Default = 1536` | Enforced in code, not just by convention — a generator returning a different length fails fast instead of corrupting the column |
| Column type | `vector(1536)` | Must match the enforced dimension exactly |
| Distance metric | Cosine (`<=>`) | Similarity = `1 - (distance / 2)` in `EfCoreKnowledgeDocumentRepository` |
| Index type | HNSW | Created in the `InitialCreate` migration, `vector_cosine_ops` |
| Storage | `knowledge.documents.vector` | In the Knowledge context |

## See Also

- [patterns](references/patterns.md)
- [workflows](references/workflows.md)

## Related Skills

- See the **postgresql** skill for general PostgreSQL patterns
- See the **entity-framework** skill for EF Core mapping patterns
