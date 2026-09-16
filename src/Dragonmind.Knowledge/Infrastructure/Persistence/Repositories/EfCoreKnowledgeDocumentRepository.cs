using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Domain.Aggregates.KnowledgeDocumentAggregate;
using Dragonmind.Knowledge.Domain.Repositories;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using Pgvector;

namespace Dragonmind.Knowledge.Infrastructure.Persistence.Repositories;

/// <summary>
/// Entity Framework Core repository for knowledge documents using pgvector for semantic search.
/// Uses KnowledgeDbContext with direct domain entity mapping.
/// </summary>
public sealed class EfCoreKnowledgeDocumentRepository : IKnowledgeDocumentRepository
{
    private readonly IDbContextFactory<KnowledgeDbContext> _contextFactory;

    public EfCoreKnowledgeDocumentRepository(IDbContextFactory<KnowledgeDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public async Task<KnowledgeDocument?> GetByIdAsync(
        DocumentId id,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Documents
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
    }

    public async Task<IReadOnlyList<KnowledgeDocument>> GetByScopeIdAsync(
        ScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Documents
            .AsNoTracking()
            .Where(d => d.ScopeId == scopeId)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(
        KnowledgeDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document, nameof(document));

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        await context.Documents.AddAsync(document, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(
        KnowledgeDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document, nameof(document));

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        context.Documents.Update(document);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(
        DocumentId id,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var document = await context.Documents
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

        if (document != null)
        {
            context.Documents.Remove(document);
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<(KnowledgeDocument Document, double Similarity)>> SearchBySimilarityAsync(
        float[] queryVector,
        int maxResults = 5,
        double minSimilarity = 0.0,
        ScopeId? scopeId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queryVector, nameof(queryVector));

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        var connectionOpenedByUs = connection.State != System.Data.ConnectionState.Open;

        try
        {
            if (connectionOpenedByUs)
            {
                await connection.OpenAsync(cancellationToken);
            }

            // Use pgvector's <=> cosine distance operator server-side.
            // Normalize cosine distance [0,2] into similarity [0,1]:
            //   similarity = 1 - (distance / 2) = 1 - ((vector <=> queryVector) / 2).
            // Filter by normalized minSimilarity and LIMIT before returning to avoid loading all rows.
            //
            // The scope predicate is composed in rather than written as "@scopeId IS NULL OR
            // scope_id = @scopeId": a constant-folded OR keeps the planner from using
            // ix_documents_scope_id, and an untyped null parameter cannot be bound to uuid.
            // Omitting it entirely searches EVERY scope's documents — rows are ranked purely by
            // cosine distance, so without this filter one scope's documents compete for the same
            // LIMIT slots as another's.
            //
            // A scope-scoped search and an unscoped one need DIFFERENT query shapes, because the
            // right index differs and getting it wrong fails SILENTLY.
            //
            // ix_documents_vector_hnsw covers `vector` only. Once one scope holds enough rows, the
            // planner prefers that index and the scope predicate becomes a POST-filter applied
            // inside a bounded ANN candidate window — so the scan can exhaust its budget on other
            // scopes' rows and return fewer than LIMIT, or nothing at all, while thousands of this
            // scope's rows qualify. No error, no log: the caller simply gets fewer results, or none.
            //
            // Measured on pgvector 0.8.2 (m=16, ef_construction=64, vector_cosine_ops) at 25k rows
            // with 5k in one scope:
            //   - 200 rows/scope   -> Bitmap Heap Scan + top-N sort, correct.
            //   - 5,000 rows/scope -> Index Scan using the HNSW index; 12 of 30 random query
            //     vectors returned FEWER than the requested 5, one plan returned 0 of 5.
            //   - `SET hnsw.iterative_scan = strict_order` did NOT fix it (still 12 of 30).
            //   - The MATERIALIZED CTE below: 0 of 30 under-fetched, 75 ms at 5,000 rows.
            //
            // MATERIALIZED is load-bearing, not decoration: without it the planner is free to inline
            // the CTE and collapse back to the filtered-ANN plan. Gathering the scope's rows first
            // and sorting them exactly costs O(rows in THIS scope) — bounded by scope size, not by
            // how many scopes exist — and is exact rather than approximate, which is what a top-5
            // over a few thousand rows should be anyway.
            //
            // The unscoped shape keeps the plain ANN query. It is left as-is deliberately —
            // materialising an unscoped search would mean gathering EVERY scope's rows, which is
            // strictly worse — and it is unreached by both live callers, which always pass a scope.
            // If an unscoped caller ever appears at scale, it needs the same treatment as the
            // scoped path, not this one.
            string sql = scopeId is not null
                ? """
                    WITH scoped AS MATERIALIZED (
                        SELECT id, vector
                        FROM knowledge.documents
                        WHERE vector IS NOT NULL AND scope_id = @scopeId
                    )
                    SELECT id, 1.0 - ((vector <=> @queryVector) / 2.0) AS similarity
                    FROM scoped
                    WHERE 1.0 - ((vector <=> @queryVector) / 2.0) >= @minSimilarity
                    ORDER BY vector <=> @queryVector ASC
                    LIMIT @maxResults
                    """
                : """
                    SELECT id, 1.0 - ((vector <=> @queryVector) / 2.0) AS similarity
                    FROM knowledge.documents
                    WHERE vector IS NOT NULL
                      AND 1.0 - ((vector <=> @queryVector) / 2.0) >= @minSimilarity
                    ORDER BY vector <=> @queryVector ASC
                    LIMIT @maxResults
                    """;

            await using var cmd = (NpgsqlCommand)connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("queryVector", new Vector(queryVector));
            cmd.Parameters.AddWithValue("minSimilarity", minSimilarity);
            cmd.Parameters.AddWithValue("maxResults", maxResults);
            if (scopeId is not null)
            {
                cmd.Parameters.AddWithValue("scopeId", scopeId.Value);
            }

            // Collect ranked (id, similarity) pairs from the server-side query.
            var ranked = new List<(Guid Id, double Similarity)>();
            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var id = reader.GetGuid(0);
                    var similarity = reader.GetDouble(1);
                    ranked.Add((id, similarity));
                }
            }

            if (ranked.Count == 0)
            {
                return [];
            }

            // Load the full KnowledgeDocument entities via EF Core.
            // The set is already bounded to maxResults rows so this is O(maxResults), not O(n).
            // EF Core cannot translate .Contains() on value object properties (DocumentId.Value),
            // so a single WHERE ... IN query is not an option here. Individual FindAsync calls
            // also hit the identity map for already-tracked documents — safe since maxResults
            // is typically ≤ 5.
            var documentDict = new Dictionary<Guid, KnowledgeDocument>();
            foreach (var (id, _) in ranked)
            {
                var doc = await context.Documents.FindAsync(new object[] { DocumentId.From(id) }, cancellationToken);
                if (doc != null)
                    documentDict[id] = doc;
            }

            // Re-pair in server-determined rank order (closest first).
            var results = new List<(KnowledgeDocument Document, double Similarity)>(ranked.Count);
            foreach (var (id, similarity) in ranked)
            {
                if (documentDict.TryGetValue(id, out var document))
                {
                    results.Add((document, similarity));
                }
            }

            return results;
        }
        finally
        {
            if (connectionOpenedByUs && connection.State == System.Data.ConnectionState.Open)
            {
                await connection.CloseAsync();
            }
        }
    }
}
