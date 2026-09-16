using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Domain.Aggregates.KnowledgeDocumentAggregate;

namespace Dragonmind.Knowledge.Domain.DomainServices;

/// <summary>
/// Domain service for performing vector-based semantic search.
/// </summary>
public interface IVectorSearchService
{
    /// <summary>
    /// Searches for documents similar to the given query text.
    /// Returns documents ordered by relevance (cosine similarity).
    /// </summary>
    /// <param name="queryText">The text to search for.</param>
    /// <param name="maxResults">Maximum number of results to return.</param>
    /// <param name="minSimilarity">Minimum similarity threshold (0.0 to 1.0).</param>
    /// <param name="scopeId">When supplied, restricts the search to that scope's documents.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of documents with their similarity scores.</returns>
    Task<IReadOnlyList<(KnowledgeDocument Document, double SimilarityScore)>> SearchAsync(
        string queryText,
        int maxResults = 5,
        double minSimilarity = 0.0,
        ScopeId? scopeId = null,
        CancellationToken cancellationToken = default);
}
