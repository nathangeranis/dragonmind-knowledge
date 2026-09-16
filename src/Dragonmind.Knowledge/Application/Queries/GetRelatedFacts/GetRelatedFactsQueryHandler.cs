using Dragonmind.Core.Application;
using Dragonmind.Core.Application.AntiCorruptionLayer.DTOs;
using Dragonmind.Knowledge.Domain.DomainServices;
using Microsoft.Extensions.Logging;

namespace Dragonmind.Knowledge.Application.Queries.GetRelatedFacts;

/// <summary>
/// Handler for getting facts related to a specific entity via graph traversal.
/// </summary>
/// <remarks>
/// The traversal distance is carried on <see cref="KnowledgeFactDto.Distance"/> rather than
/// alongside the DTO in a tuple. Returning both meant the DTO's own copy could be left unset while
/// the tuple looked right, so anything dispatching this query through <c>IMediator</c> instead of
/// going through the facade read 0 for every fact.
/// </remarks>
public sealed class GetRelatedFactsQueryHandler : IQueryHandler<GetRelatedFactsQuery, IReadOnlyList<KnowledgeFactDto>>
{
    private readonly IGraphTraversalService _graphTraversalService;
    private readonly ILogger<GetRelatedFactsQueryHandler> _logger;

    public GetRelatedFactsQueryHandler(
        IGraphTraversalService graphTraversalService,
        ILogger<GetRelatedFactsQueryHandler> logger)
    {
        _graphTraversalService = graphTraversalService;
        _logger = logger;
    }

    public async Task<IReadOnlyList<KnowledgeFactDto>> HandleAsync(
        GetRelatedFactsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query, nameof(query));

        try
        {
            // Use the domain service to traverse the graph
            var results = await _graphTraversalService.GetRelatedFactsAsync(
                query.EntityName,
                query.ScopeId,
                query.MaxDepth,
                cancellationToken);

            // The domain service returns (fact, distance) pairs; this is the one place that
            // folds the distance into the DTO.
            return results
                .Select(r => new KnowledgeFactDto
                {
                    FactId = r.Fact.Id.Value,
                    ScopeId = r.Fact.ScopeId,
                    Subject = r.Fact.Subject.Name,
                    SubjectType = r.Fact.Subject.EntityType,
                    Predicate = r.Fact.Predicate,
                    Object = r.Fact.Object.Name,
                    ObjectType = r.Fact.Object.EntityType,
                    Timestamp = r.Fact.Timestamp,
                    Distance = r.Distance
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Knowledge graph traversal failed for entity '{Entity}'. Returning empty facts.",
                query.EntityName);
            return Array.Empty<KnowledgeFactDto>();
        }
    }
}
