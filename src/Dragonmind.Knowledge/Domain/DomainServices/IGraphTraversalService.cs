using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Domain.Aggregates.KnowledgeGraphAggregate;

namespace Dragonmind.Knowledge.Domain.DomainServices;

/// <summary>
/// Domain service for traversing the knowledge graph.
/// </summary>
public interface IGraphTraversalService
{
    /// <summary>
    /// Finds all facts related to a given entity name.
    /// Traverses the graph to find connected entities up to maxDepth.
    /// </summary>
    /// <param name="entityName">The name of the entity to start from.</param>
    /// <param name="scopeId">
    /// The scope whose facts to search. Required rather than defaulted: the graph is a single
    /// shared store, so an omitted scope would let another scope's facts about the same entity
    /// name leak into this one's traversal.
    /// </param>
    /// <param name="maxDepth">Maximum depth to traverse (default: 2).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of facts related to the entity, ordered by distance.</returns>
    Task<IReadOnlyList<(KnowledgeFact Fact, int Distance)>> GetRelatedFactsAsync(
        string entityName,
        ScopeId scopeId,
        int maxDepth = 2,
        CancellationToken cancellationToken = default);
}
