using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Domain.Aggregates.KnowledgeGraphAggregate;
using Dragonmind.Knowledge.Domain.DomainServices;
using Dragonmind.Knowledge.Domain.Repositories;

namespace Dragonmind.Knowledge.Infrastructure.DomainServices;

/// <summary>
/// Domain service for traversing the knowledge graph.
/// </summary>
public sealed class GraphTraversalService : IGraphTraversalService
{
    private readonly IKnowledgeGraphRepository _repository;

    public GraphTraversalService(IKnowledgeGraphRepository repository)
    {
        _repository = repository;
    }

    public async Task<IReadOnlyList<(KnowledgeFact Fact, int Distance)>> GetRelatedFactsAsync(
        string entityName,
        ScopeId scopeId,
        int maxDepth = 2,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName, nameof(entityName));
        ArgumentNullException.ThrowIfNull(scopeId, nameof(scopeId));

        // Use the repository's graph traversal method
        var results = await _repository.GetFactsWithinDistanceAsync(
            entityName,
            scopeId,
            maxDepth,
            cancellationToken);

        return results;
    }
}
