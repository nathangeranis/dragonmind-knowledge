using Dragonmind.Core.Application;
using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Application.DTOs;
using Dragonmind.Knowledge.Domain.DomainServices;

namespace Dragonmind.Knowledge.Application.Queries.SearchKnowledge;

/// <summary>
/// Handler for searching knowledge documents using semantic similarity.
/// </summary>
public sealed class SearchKnowledgeQueryHandler : IQueryHandler<SearchKnowledgeQuery, IReadOnlyList<VectorSearchResultDto>>
{
    private readonly IVectorSearchService _vectorSearchService;

    public SearchKnowledgeQueryHandler(IVectorSearchService vectorSearchService)
    {
        _vectorSearchService = vectorSearchService;
    }

    public async Task<IReadOnlyList<VectorSearchResultDto>> HandleAsync(
        SearchKnowledgeQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query, nameof(query));

        // Use the domain service to perform the search
        var results = await _vectorSearchService.SearchAsync(
            query.QueryText,
            query.MaxResults,
            query.MinSimilarity,
            query.ScopeId is { } id ? ScopeId.From(id) : null,
            cancellationToken);

        // Convert to DTOs
        return results
            .Select(r => new VectorSearchResultDto(
                new KnowledgeDocumentDto(
                    r.Document.Id,
                    r.Document.ScopeId,
                    r.Document.Content.Value,
                    r.Document.Content.Source,
                    r.Document.Content.Category,
                    r.Document.Timestamp,
                    r.Document.Embedding != null),
                r.SimilarityScore))
            .ToList();
    }
}
