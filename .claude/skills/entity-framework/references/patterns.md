# EF Core Patterns Reference

## Contents
- DbContext Setup
- Schema and Migration History Isolation
- DI Registration: Pooled Factory + NpgsqlDataSource
- Entity Configuration (snake_case + Value Objects)
- The Vector Property Needs a ValueComparer
- pgvector Integration
- Repository Implementation
- Anti-Patterns

---

## DbContext Setup

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

---

## Schema and Migration History Isolation

Even with one context, two settings keep this schema self-contained rather than everything landing
in `public`:

1. `modelBuilder.HasDefaultSchema("knowledge")` in `OnModelCreating` — every table this context maps
   lands under `knowledge.*` unless an individual configuration overrides it.
2. `npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "knowledge")` in
   `AddKnowledgeContext` and in the design-time factory — the applied-migrations bookkeeping table
   itself also lives in `knowledge`, not in `public`.

If this repository ever grows a second bounded context sharing the same database, both settings are
what would let its DbContext and migration history coexist without colliding with this one's.

---

## DI Registration: Pooled Factory + NpgsqlDataSource

pgvector and Apache AGE extensions are registered at the *data-source* level, not the DbContext
options level, so `AddKnowledgeContext` builds one `NpgsqlDataSource` via `KnowledgeDataSource.Create`
and registers a **pooled context factory** against it, rather than a plain `AddDbContext`:

```csharp
// KnowledgeDataSource.cs
public static NpgsqlDataSource Create(string connectionString)
    => new NpgsqlDataSourceBuilder(connectionString).UseVector().Build();

// Infrastructure/DI/KnowledgeContextExtensions.cs
public static IServiceCollection AddKnowledgeContext(
    this IServiceCollection services,
    NpgsqlDataSource dataSource,
    Action<KnowledgeOptions>? configure = null)
{
    services.AddPooledDbContextFactory<KnowledgeDbContext>(options =>
        options.UseNpgsql(dataSource, npgsqlOptions =>
            {
                npgsqlOptions.UseVector();
                npgsqlOptions.MigrationsHistoryTable("__EFMigrationsHistory", "knowledge");
            })
            .UseSnakeCaseNamingConvention());
    // ... MediatR scan, behaviors, repositories, services, caching decorators
    return services;
}
```

Consumers inject `IDbContextFactory<KnowledgeDbContext>` (not the context directly) and call
`CreateDbContextAsync()` per operation.

---

## Entity Configuration (snake_case + Value Objects)

Use `IEntityTypeConfiguration<T>` — keeps the DbContext clean and supports
`ApplyConfigurationsFromAssembly`.

```csharp
// src/Dragonmind.Knowledge/Infrastructure/Persistence/EntityConfigurations/KnowledgeDocumentEntityConfiguration.cs
public sealed class KnowledgeDocumentEntityConfiguration : IEntityTypeConfiguration<KnowledgeDocument>
{
    public void Configure(EntityTypeBuilder<KnowledgeDocument> builder)
    {
        builder.ToTable("documents");   // → knowledge.documents
        builder.HasKey(e => e.Id);

        // Value object ID conversion
        builder.Property(e => e.Id)
            .HasConversion(
                id => id.Value,
                value => DocumentId.From(value))
            .HasColumnName("id");

        builder.Property(e => e.ScopeId)
            .HasConversion(
                id => id.Value,
                value => ScopeId.From(value))
            .HasColumnName("scope_id");

        // DocumentContent is an OWNED value object, not a single converted column: it carries
        // Value, Source and Category, so it maps to three columns on the same row. A single
        // .HasConversion() here would silently drop Source and Category.
        builder.OwnsOne(e => e.Content, content =>
        {
            content.Property(c => c.Value)
                .HasColumnName("content")
                .HasColumnType("text");

            content.Property(c => c.Source)
                .HasColumnName("source")
                .HasColumnType("text");

            content.Property(c => c.Category)
                .HasColumnName("category")
                .HasColumnType("text");
        });

        // Owned value object (no separate table)
        builder.OwnsOne(e => e.Embedding, embedding =>
        {
            embedding.Property(e => e.Vector)
                .HasColumnName("vector")
                .HasColumnType($"vector({EmbeddingDimensions.Default})")
                .HasConversion(v => new Pgvector.Vector(v), v => v.ToArray());
        });
    }
}
```

---

## The Vector Property Needs a ValueComparer

### WARNING: A `float[]`-backed property without a `ValueComparer` silently loses updates

EF Core's change tracker compares reference-typed properties like `float[]` by reference by default.
It cannot see an in-place mutation, and — this is the dangerous part — **`SaveChanges` then silently
skips the column instead of failing**. Because `Embedding` is an owned value object and
`KnowledgeDocument.UpdateContent` replaces the whole `Embedding` instance rather than mutating the
array in place, the replacement is usually enough for change tracking to notice on its own. Register
the comparer anyway so a future change that mutates the array in place doesn't quietly stop
persisting:

```csharp
var vectorProperty = embedding.Property(e => e.Vector)
    .HasColumnName("vector")
    .HasColumnType($"vector({EmbeddingDimensions.Default})")
    .HasConversion(v => new Pgvector.Vector(v), v => v.ToArray());

vectorProperty.Metadata.SetValueComparer(
    new ValueComparer<float[]>(
        (a, b) => (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b)),
        a => a == null ? 0 : a.Aggregate(0, (h, v) => HashCode.Combine(h, v)),
        a => a == null ? Array.Empty<float>() : a.ToArray()));
```

Without this, a hypothetical future code path that does `document.Embedding.Vector[0] = 0f` in place
would compile, run, and save silently as a no-op — no exception, no warning.

---

## pgvector Integration

`vector` is enabled two ways: on the model (`modelBuilder.HasPostgresExtension("vector")`, in
`KnowledgeDbContext.OnModelCreating`) and on the data source
(`new NpgsqlDataSourceBuilder(cs).UseVector()`, in `KnowledgeDataSource.Create`). Neither one alone
is enough — `HasPostgresExtension` only tells EF Core migrations to emit `CREATE EXTENSION`; the
data-source `UseVector()` is what actually lets Npgsql translate `Pgvector.Vector` at the wire level.

The embedding itself is an **owned value object** with a `Pgvector.Vector` conversion — not a plain
top-level property:

```csharp
builder.OwnsOne(e => e.Embedding, embedding =>
{
    embedding.Property(e => e.Vector)
        .HasColumnName("vector")
        .HasColumnType($"vector({EmbeddingDimensions.Default})")
        .HasConversion(
            vector => new Pgvector.Vector(vector),
            vector => vector.ToArray());
});
```

HNSW index SQL (in the `InitialCreate` migration):

```csharp
migrationBuilder.Sql("""
    CREATE INDEX IF NOT EXISTS ix_documents_vector_hnsw
    ON knowledge.documents
    USING hnsw (vector vector_cosine_ops)
    WITH (m = 16, ef_construction = 64);
    """);
```

Cosine ops (`vector_cosine_ops`), explicit `IF NOT EXISTS`, and tuning params — no `CONCURRENTLY`
(migrations run inside a transaction).

---

## Repository Implementation

Repositories live in `Infrastructure/Persistence/Repositories/` and implement the Domain-layer
interface.

```csharp
// src/Dragonmind.Knowledge/Infrastructure/Persistence/Repositories/EfCoreKnowledgeDocumentRepository.cs
public sealed class EfCoreKnowledgeDocumentRepository : IKnowledgeDocumentRepository
{
    private readonly IDbContextFactory<KnowledgeDbContext> _contextFactory;

    public EfCoreKnowledgeDocumentRepository(IDbContextFactory<KnowledgeDbContext> contextFactory)
        => _contextFactory = contextFactory;

    public async Task<KnowledgeDocument?> GetByIdAsync(DocumentId id, CancellationToken ct = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);
        return await context.Documents.FirstOrDefaultAsync(d => d.Id == id, ct);
    }

    public async Task AddAsync(KnowledgeDocument document, CancellationToken ct = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(ct);
        await context.Documents.AddAsync(document, ct);
        await context.SaveChangesAsync(ct);
    }
}
```

---

## Anti-Patterns

### WARNING: Direct DbContext in Handlers

**The Problem:**
```csharp
// BAD — bypasses the repository, makes the handler untestable without a real database
public class SearchKnowledgeQueryHandler(KnowledgeDbContext context) // ❌
{
    public async Task<...> HandleAsync(SearchKnowledgeQuery query, CancellationToken ct)
        => await context.Documents.Where(...).ToListAsync(ct); // bypasses IKnowledgeDocumentRepository
}
```

**Why This Breaks:**
1. Leaks infrastructure concerns into the Application layer
2. Unit tests require a real database — Moq cannot fake a DbContext meaningfully
3. Exposes EF change-tracking behavior unpredictably to code that shouldn't have to care about it

**The Fix:**
```csharp
// GOOD — depend on the repository interface
public class SearchKnowledgeQueryHandler(IKnowledgeDocumentRepository repository) // ✅
{
    public async Task<...> HandleAsync(SearchKnowledgeQuery query, CancellationToken ct)
        => await repository.SearchBySimilarityAsync(query.Embedding, query.ScopeId, query.MaxResults, ct);
}
```

### WARNING: SaveChangesAsync in Every Repository Method

**The Problem:** Calling `SaveChangesAsync` in every repository method causes multiple round-trips
when a handler modifies more than one aggregate in the same operation.

**The Fix:** This codebase accepts per-operation saves for simplicity (each command handler modifies
one aggregate). Do NOT call `SaveChanges` from a handler directly; keep it inside the repository.
