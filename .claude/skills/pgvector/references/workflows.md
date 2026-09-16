# pgvector Workflows Reference

## Contents
- Add a New Vectorized Document Type
- How Similarity Search Already Works (+ remaining tuning options)
- Migrate Embedding Dimensions
- Troubleshooting Common Failures

---

## Add a New Vectorized Document Type

Use this when you need a new aggregate that should support semantic search alongside
`KnowledgeDocument`.

Copy this checklist and track progress:
- [ ] Step 1: Create the domain aggregate with a nullable `Embedding` property
- [ ] Step 2: Create the repository interface in the Domain layer
- [ ] Step 3: Configure `HasColumnType($"vector({EmbeddingDimensions.Default})")` in the entity configuration
- [ ] Step 4: Implement the repository with `SearchBySimilarityAsync` (mirror the raw-SQL `<=>` pattern in `EfCoreKnowledgeDocumentRepository`, including the scoped `MATERIALIZED` CTE if the new type is scope-filtered)
- [ ] Step 5: Add a `DbSet<>` to `KnowledgeDbContext`
- [ ] Step 6: Create a migration (include an HNSW index — see below)
- [ ] Step 7: Register in `AddKnowledgeContext`
- [ ] Step 8: Expose via `IKnowledgeContextFacade` if the new type needs to cross the boundary

**Step 1 — Aggregate:**

```csharp
public sealed class ReferenceNote : AggregateRoot<ReferenceNoteId>
{
    public string Title { get; private set; } = string.Empty;
    public string Body { get; private set; } = string.Empty;
    public Embedding? Embedding { get; private set; }  // Nullable until generated

    public void SetEmbedding(Embedding embedding) => Embedding = embedding;
    public void UpdateBody(string newBody)
    {
        Body = newBody;
        Embedding = null;  // Always clear on content change
    }
}
```

**Step 3 — EF Core configuration:**

```csharp
builder.OwnsOne(e => e.Embedding, emb =>
{
    emb.Property(e => e.Vector)
        .HasColumnName("vector")
        .HasColumnType($"vector({EmbeddingDimensions.Default})")
        .HasConversion(
            v => new Pgvector.Vector(v),
            v => v.ToArray());
});
```

**Step 6 — Migration:**

```bash
dotnet ef migrations add AddReferenceNoteVector \
    --project src/Dragonmind.Knowledge \
    --startup-project src/Dragonmind.Knowledge \
    --context KnowledgeDbContext
```

Add an HNSW index in the migration `Up()` (mirror the index created in `InitialCreate`):

```csharp
migrationBuilder.Sql("""
    CREATE INDEX IF NOT EXISTS ix_reference_notes_vector_hnsw
    ON knowledge.reference_notes
    USING hnsw (vector vector_cosine_ops)
    WITH (m = 16, ef_construction = 64);
    """);
```

Validate:
1. Apply the migration: `docker compose up -d --wait db`, then run `dotnet ef database update` (see the **postgresql** skill) with `KNOWLEDGE_DB_CONNECTION` set
2. Check the column: `\d knowledge.reference_notes` in `psql` → should show `vector vector(1536)`
3. Insert a test row, query it back, verify the vector round-trips correctly
4. If any step fails, fix before marking complete

---

## How Similarity Search Already Works (+ remaining tuning options)

Raw-SQL cosine search and the HNSW index are **already shipped** — do not re-implement them:

- The `InitialCreate` migration creates `knowledge.documents` with the `vector(1536)` column and
  `ix_documents_vector_hnsw` (`hnsw`, `vector_cosine_ops`, `m = 16, ef_construction = 64`)
- `EfCoreKnowledgeDocumentRepository.SearchBySimilarityAsync` runs `vector <=> @queryVector`
  server-side, using the `MATERIALIZED` candidate CTE when the search is scoped (see
  [patterns](references/patterns.md)), then re-fetches ranked documents individually via `FindAsync`
  (NOT `.Contains()` — EF cannot translate it over the `DocumentId` value object)

**pgvector distance operators:**

| Operator | Distance Type | Index Type |
|----------|--------------|------------|
| `<=>` | Cosine | `vector_cosine_ops` |
| `<->` | L2 (Euclidean) | `vector_l2_ops` |
| `<#>` | Inner product | `vector_ip_ops` |

This repository uses `<=>` (cosine) — it normalizes magnitude, which matters when document lengths
vary. Similarity is normalized as `1 - (distance / 2)`.

**Still legitimately open (prospective tuning):**
- Tune `hnsw.ef_search` per query for a recall-vs-latency trade-off (the default index params
  `m=16, ef_construction=64` are safe; increase `ef_construction` for higher recall at index-build
  cost)
- Tune the CTE's over-fetch width against real scope-size distributions before assuming a fixed
  multiplier holds at every scale

---

## Migrate Embedding Dimensions

**When:** Switching the registered embedding provider to one with a different output size. All
existing vectors are incompatible — you must re-embed everything. `Embedding.Create` accepts any
1–4096 dims, so nothing in the domain layer catches a mismatch on its own — the
`EmbeddingDimensions` enforcement at the generator seam and the Postgres column are the two real
enforcement points.

Copy this checklist and track progress:
- [ ] Step 1: Update `EmbeddingDimensions.Default` and the registered provider's configuration
- [ ] Step 2: Create a migration to `ALTER COLUMN vector TYPE vector(N)`
- [ ] Step 3: Drop the old HNSW index (can't resize in place)
- [ ] Step 4: Re-embed all existing `documents` rows (background job or script)
- [ ] Step 5: Recreate the HNSW index with the new dimensions
- [ ] Step 6: Update `HasColumnType` in `KnowledgeDocumentEntityConfiguration`

**Step 2 — Migration:**

```csharp
// WARNING: This clears all existing vector data
migrationBuilder.Sql("""
    ALTER TABLE knowledge.documents
        ALTER COLUMN vector TYPE vector(768)
        USING NULL;  -- Clears existing vectors; must re-embed
    """);
```

**Re-embed script pattern:**

```csharp
// Run as a one-off background job or CLI tool
var documents = await repository.GetAllWithoutEmbeddingAsync();
foreach (var batch in documents.Chunk(50))
{
    foreach (var doc in batch)
    {
        var embedding = await embeddingService.GenerateEmbeddingAsync(doc.Content.Value, ct);
        doc.SetEmbedding(embedding);
        await repository.UpdateAsync(doc, ct);
    }
    await Task.Delay(200, ct);  // Respect the provider's rate limit
}
```

---

## Troubleshooting Common Failures

### `InvalidCastException: Cannot write value of type 'Single[]' to column`

**Cause:** `UseVector()` missing — either `npgsqlOptions.UseVector()` in `AddKnowledgeContext` /
`KnowledgeDbContextFactory`, or the data-source builder's `UseVector()` in `KnowledgeDataSource.Create`.

**Fix:** Ensure BOTH layers call it:
```csharp
dataSourceBuilder.UseVector();                                    // KnowledgeDataSource.Create
options.UseNpgsql(dataSource, npgsql => npgsql.UseVector());      // EF options
```

---

### `ERROR: expected 1536 dimensions, not N`

**Cause:** The embedding provider's output dimension doesn't match the column definition.
`Embedding.Create` won't catch this (it accepts 1–4096 dims).

**Fix:** Confirm `EmbeddingDimensions.Default` matches `HasColumnType("vector(N)")`, and that the
registered provider is actually returning that many dimensions — log the vector length in
`EmbeddingService.GenerateEmbeddingAsync` to confirm.

---

### Search returns 0 results despite data existing

**Cause:** Either `minSimilarity` is too high, embeddings are null (stored before `SetEmbedding` was
called), or — for a scoped search — the ANN over-fetch width is too narrow relative to how many
documents belong to other scopes (see the `MATERIALIZED` CTE discussion above).

**Fix:**
```sql
-- Check how many rows have a non-null vector
SELECT COUNT(*) FROM knowledge.documents WHERE vector IS NOT NULL;
```
```csharp
// Test with 0 threshold
var results = await _knowledge.SearchKnowledgeAsync(query, scopeId, maxResults: 10, minSimilarity: 0.0);
```

---

### Migration fails: `type "vector" does not exist`

**Cause:** The `vector` extension wasn't created before the rest of the migration ran.

**Fix:** The `InitialCreate` migration creates the extension itself
(`CREATE EXTENSION IF NOT EXISTS vector;`) as its first statement — if you're hitting this, confirm
you're running `dotnet ef database update` against a fresh database rather than jumping straight to
a later migration state.

See the **postgresql** skill for extension management and the **entity-framework** skill for
migration ordering issues.
