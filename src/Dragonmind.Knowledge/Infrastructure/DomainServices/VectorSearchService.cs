using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Domain.ValueObjects;
using Dragonmind.Knowledge.Domain.Aggregates.KnowledgeDocumentAggregate;
using Dragonmind.Knowledge.Domain.DomainServices;
using Dragonmind.Knowledge.Domain.Repositories;

namespace Dragonmind.Knowledge.Infrastructure.DomainServices;

/// <summary>
/// Domain service for performing vector-based semantic search.
/// </summary>
public sealed class VectorSearchService : IVectorSearchService
{
    private readonly IKnowledgeDocumentRepository _repository;
    private readonly IEmbeddingService _embeddingService;

    public VectorSearchService(
        IKnowledgeDocumentRepository repository,
        IEmbeddingService embeddingService)
    {
        _repository = repository;
        _embeddingService = embeddingService;
    }

    public async Task<IReadOnlyList<(KnowledgeDocument Document, double SimilarityScore)>> SearchAsync(
        string queryText,
        int maxResults = 5,
        double minSimilarity = 0.0,
        ScopeId? scopeId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryText, nameof(queryText));

        // Generate embedding for the query
        var queryEmbedding = await _embeddingService.GenerateEmbeddingAsync(
            queryText,
            cancellationToken);

        // Search using the repository
        var results = await _repository.SearchBySimilarityAsync(
            queryEmbedding.Vector,
            maxResults,
            minSimilarity,
            scopeId,
            cancellationToken);

        return results;
    }
}
