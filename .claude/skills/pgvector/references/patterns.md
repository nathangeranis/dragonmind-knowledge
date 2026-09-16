# pgvector Patterns Reference

## Contents
- EF Core Column Mapping
- Embedding Value Object Pattern
- Repository Similarity Search (raw SQL + HNSW, scope-filtered)
- Anti-Patterns
- The Embedding Generation Seam

---

## EF Core Column Mapping

pgvector requires `UseVector()` in **both** the data-source builder and the EF Core options, plus
the correct `HasColumnType` + conversion. Missing any of these silently breaks vector storage.

```csharp
// src/Dragonmind.Knowledge/Infrastructure/Persistence/EntityConfigurations/KnowledgeDocumentEntityConfiguration.cs
builder.OwnsOne(e => e.Embedding, embedding =>
{
    embedding.Property(e => e.Vector)
        .HasColumnName("vector")
        .HasColumnType("vector(1536)")          // Must match the enforced embedding dimension
        .HasConversion(
            v => new Pgvector.Vector(v),        // EF Core write: float[] → pgvector type
            v => v.ToArray());                  // EF Core read: pgvector type → float[]
});
```

```csharp
// 1) KnowledgeDataSource.Create — builds the shared NpgsqlDataSource
public static NpgsqlDataSource Create(string connectionString)
{
    var builder = new NpgsqlDataSourceBuilder(connectionString);
    return builder.UseVector().Build();
}

// 2) AddKnowledgeContext (Infrastructure/DI/KnowledgeContextExtensions.cs)
services.AddPooledDbContextFactory<KnowledgeDbContext>(options =>
    options.UseNpgsql(dataSource, npgsqlOptions =>
    {
        npgsqlOptions.UseVector();   // Registers the Pgvector.EntityFrameworkCore plugin
    }));

// 3) Also mirrored in KnowledgeDbContextFactory.cs (design-time factory for migrations)
```

**Dimension mismatch kills everything.** If the embedding generator returns a vector length
different from `vector(1536)`, PostgreSQL rejects every INSERT at runtime, not at compile time.

---

## Embedding Value Object Pattern

The `Embedding` domain value object wraps `float[]` with validation. Never pass a raw `float[]`
across the aggregate boundary — always use the value object.

```csharp
// src/Dragonmind.Knowledge/Domain/ValueObjects/Embedding.cs
public sealed class Embedding : ValueObject
{
    public const int MinDimensions = 1;
    public const int MaxDimensions = 4096;

    public float[] Vector { get; private set; }
    public int Dimensions => Vector.Length;

    public static Embedding Create(float[] vector)
    {
        // Validates: not null, 1–4096 dims, no NaN/Infinity
        return new Embedding((float[])vector.Clone());
    }
}
```

**WARNING:** `Embedding.Create` only range-checks 1–4096 dimensions — it does NOT assert the exact
1536 the column expects. The dimension guard lives at the generator seam, not in the value object:
`ExtensionsAIEmbeddingGenerator` requests `EmbeddingDimensions.Default` from the underlying
generator and truncates/renormalizes or rejects a mismatched result before an `Embedding` is ever
constructed (see *The Embedding Generation Seam* below). If some future code path constructs an
`Embedding` directly from a length that skipped that seam, nothing catches the mismatch until the
Postgres write.

```csharp
// KnowledgeDocument clears the embedding when content changes — prevents stale vectors
public void UpdateContent(DocumentContent newContent)
{
    Content = newContent;
    Embedding = null;  // Force regeneration on next search
}
```

---

## Repository Similarity Search (raw SQL + HNSW, scope-filtered)

`EfCoreKnowledgeDocumentRepository.SearchBySimilarityAsync` pushes the similarity query to Postgres
using the `<=>` cosine distance operator, with `ORDER BY`/`LIMIT` server-side. The HNSW index
(`vector_cosine_ops`), created in the `InitialCreate` migration, keeps the ordering fast even over a
large table.

When the search is scoped to one caller, the query cannot just add `AND scope_id = @scope` to a
plain `ORDER BY vector <=> @q LIMIT @n` query and stop there. An HNSW index answers "nearest
neighbors" approximately and hands back its candidates *before* any other predicate is applied — if
the nearest `@n` neighbors by embedding distance happen to mostly belong to other scopes, the
filtered result set under-fetches, sometimes down to zero rows, even though enough matching
documents exist further out in the ranking. The fix is a two-step, `MATERIALIZED` CTE: over-fetch a
wider ANN candidate set first, stop Postgres from folding the scope filter into that step, then
filter and re-rank the smaller candidate set:

```sql
WITH candidates AS MATERIALIZED (
    SELECT id, vector <=> @queryVector AS distance
    FROM knowledge.documents
    WHERE vector IS NOT NULL
    ORDER BY vector <=> @queryVector ASC
    LIMIT @overFetch          -- wider than @maxResults, e.g. 5-10x
)
SELECT c.id, 1.0 - (c.distance / 2.0) AS similarity
FROM candidates c
JOIN knowledge.documents d ON d.id = c.id
WHERE d.scope_id = @scopeId
  AND 1.0 - (c.distance / 2.0) >= @minSimilarity
ORDER BY c.distance ASC
LIMIT @maxResults;
```

`MATERIALIZED` matters here: without it, the query planner is free to push the `scope_id` predicate
down into the candidates CTE (Postgres 12+ can inline a non-materialized CTE), which recreates the
exact under-fetch problem the two-step shape exists to avoid.

After the ranked `(id, similarity)` pairs come back, the repository re-fetches each
`KnowledgeDocument` **individually via `context.Documents.FindAsync(DocumentId.From(id))` in rank
order**. This is deliberate: EF Core cannot translate `.Contains()` over the `DocumentId` value
object (see Anti-Patterns), and the result set is already bounded to `maxResults` (typically ≤ 5),
so per-row `FindAsync` is O(maxResults) and benefits from the identity map.

---

## Anti-Patterns

### WARNING: Missing `UseVector()` Registration (either layer)

**The Problem:**

```csharp
// BAD — UseVector() omitted from EF options...
services.AddPooledDbContextFactory<KnowledgeDbContext>(options =>
    options.UseNpgsql(dataSource));
// ...or omitted from KnowledgeDataSource.Create's NpgsqlDataSourceBuilder
```

**Why This Breaks:**
1. Npgsql cannot map `Pgvector.Vector` → throws `InvalidCastException` at runtime
2. EF Core migrations generate `bytea` instead of `vector(1536)` for the column
3. All embedding reads return null; all writes silently fail

**The Fix:** call `UseVector()` in BOTH places — the data-source builder in `KnowledgeDataSource.Create`
AND `npgsqlOptions.UseVector()` in the EF options (plus the design-time `KnowledgeDbContextFactory`).

---

### WARNING: `.Contains()` Re-Fetch Over Value-Object IDs

**The Problem:**

```csharp
// BAD — looks efficient, does not work
var documents = await context.Documents
    .Where(d => documentIds.Contains(d.Id.Value))   // EF cannot translate this
    .ToListAsync(cancellationToken);
```

**Why This Breaks:** EF Core cannot translate `.Contains()` over the `DocumentId` value-object
property — the query throws at runtime. The real repository instead re-fetches each document via
`FindAsync` in rank order (see above). Do not "optimize" it back to `.Contains()`.

---

### WARNING: Hardcoded Embedding Dimensions

**The Problem:**

```csharp
// BAD — magic number with no enforcement
.HasColumnType("vector(1536)")
// ... but the embedding generator switches to a model returning 768-dim vectors
```

**Why This Breaks:**
1. PostgreSQL throws `ERROR: expected 1536 dimensions, not 768` on every INSERT
2. The mismatch is undetectable until runtime unless the seam enforces it (see below) — `Embedding.Create` alone accepts any 1–4096 dims

**The Fix:** Reference `EmbeddingDimensions.Default` from the column mapping instead of a bare
literal, and let `ExtensionsAIEmbeddingGenerator` enforce the dimension at the seam (see below).

---

## The Embedding Generation Seam

`EmbeddingService` (`src/Dragonmind.Knowledge/Infrastructure/Embeddings/EmbeddingService.cs`) depends only
on `IEmbeddingGenerator` (`Dragonmind.Core`) — an abstraction over
`Microsoft.Extensions.AI.IEmbeddingGenerator<string, Embedding<float>>`. This repository ships no
concrete provider; the host application registers one and the Knowledge context never references it
by name.

`ExtensionsAIEmbeddingGenerator` enforces `EmbeddingDimensions.Default` (1536) against whatever the
underlying provider returns: it requests that dimension explicitly where the provider supports it,
applies truncation and L2 renormalization if the provider returns a longer vector, and a hard guard
rejects a mismatched result rather than writing a wrong-length vector to Postgres.

```csharp
// DI registration — Scoped, NOT Singleton
services.AddScoped<IEmbeddingService>(provider =>
    new EmbeddingService(provider.GetRequiredService<IEmbeddingGenerator>()));
```

See the **entity-framework** skill for owned-entity configuration patterns like `OwnsOne`.
