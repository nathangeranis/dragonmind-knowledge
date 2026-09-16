using Dragonmind.Core.Domain;
using Dragonmind.Knowledge.Domain.Aggregates.KnowledgeDocumentAggregate;

using Microsoft.EntityFrameworkCore;

namespace Dragonmind.Knowledge.Infrastructure.Persistence;

/// <summary>
/// DbContext for the Knowledge bounded context.
/// Manages KnowledgeDocument aggregate persistence with PostgreSQL + pgvector.
/// </summary>
public class KnowledgeDbContext : DbContext, IUnitOfWork
{
    public KnowledgeDbContext(DbContextOptions<KnowledgeDbContext> options)
        : base(options)
    {
    }

    public async Task<int> CommitAsync(CancellationToken cancellationToken = default)
    {
        return await SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Knowledge documents table - stores reference content with vector embeddings.
    /// </summary>
    public DbSet<KnowledgeDocument> Documents => Set<KnowledgeDocument>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasPostgresExtension("vector");

        // Apply all entity configurations from this assembly
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(KnowledgeDbContext).Assembly);

        // Configure schema for Knowledge context
        modelBuilder.HasDefaultSchema("knowledge");
    }
}
