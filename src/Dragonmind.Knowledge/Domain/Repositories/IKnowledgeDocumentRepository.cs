using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Domain.Aggregates.KnowledgeDocumentAggregate;

namespace Dragonmind.Knowledge.Domain.Repositories;

/// <summary>
/// Repository for managing knowledge documents.
/// Note: Does not expose IUnitOfWork because it uses IDbContextFactory
/// (each operation creates and disposes its own DbContext, making explicit UnitOfWork unnecessary).
/// </summary>
public interface IKnowledgeDocumentRepository
{
    /// <summary>
    /// Gets a knowledge document by its ID.
    /// </summary>
    Task<KnowledgeDocument?> GetByIdAsync(
        DocumentId id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all knowledge documents for a scope.
    /// </summary>
    Task<IReadOnlyList<KnowledgeDocument>> GetByScopeIdAsync(
        ScopeId scopeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a new knowledge document.
    /// </summary>
    Task AddAsync(
        KnowledgeDocument document,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing knowledge document.
    /// </summary>
    Task UpdateAsync(
        KnowledgeDocument document,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a knowledge document.
    /// </summary>
    Task DeleteAsync(
        DocumentId id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches for documents by vector similarity.
    /// Returns documents ordered by similarity score (highest first).
    /// </summary>
    /// <param name="scopeId">
    /// When supplied, restricts the search to documents belonging to that scope. Passing
    /// <c>null</c> searches every scope's documents, which is almost never what a caller wants:
    /// results are ranked purely by cosine distance, so one scope's documents compete for the
    /// same <paramref name="maxResults"/> slots as another's.
    /// </param>
    Task<IReadOnlyList<(KnowledgeDocument Document, double Similarity)>> SearchBySimilarityAsync(
        float[] queryVector,
        int maxResults = 5,
        double minSimilarity = 0.0,
        ScopeId? scopeId = null,
        CancellationToken cancellationToken = default);
}
