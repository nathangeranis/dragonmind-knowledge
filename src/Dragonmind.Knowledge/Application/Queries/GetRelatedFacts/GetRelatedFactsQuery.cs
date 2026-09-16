using Dragonmind.Core.Application;
using Dragonmind.Core.Application.AntiCorruptionLayer.DTOs;
using Dragonmind.Core.Domain.SharedIdentities;

namespace Dragonmind.Knowledge.Application.Queries.GetRelatedFacts;

/// <summary>
/// Query to get facts related to a specific entity in the knowledge graph.
/// </summary>
public sealed record GetRelatedFactsQuery : IQuery<IReadOnlyList<KnowledgeFactDto>>
{
    /// <summary>
    /// The name of the entity whose related facts are being requested.
    /// </summary>
    public string EntityName { get; init; }

    /// <summary>
    /// The scope whose facts to search. The knowledge graph is a single shared store, so this is
    /// required rather than defaulted — see <see cref="Domain.DomainServices.IGraphTraversalService.GetRelatedFactsAsync"/>.
    /// </summary>
    public ScopeId ScopeId { get; init; }

    /// <summary>
    /// The maximum depth to traverse when searching for related facts.
    /// Must be a positive integer.
    /// </summary>
    public int MaxDepth { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="GetRelatedFactsQuery"/> record.
    /// </summary>
    /// <param name="entityName">The name of the entity to search for. Must not be null or whitespace.</param>
    /// <param name="scopeId">The scope whose facts to search. Must not be null.</param>
    /// <param name="maxDepth">The maximum depth to traverse when searching for related facts. Must be positive.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="entityName"/> is null, empty, or consists only of white-space characters.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="scopeId"/> is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="maxDepth"/> is less than or equal to zero.
    /// </exception>
    public GetRelatedFactsQuery(string entityName, ScopeId scopeId, int maxDepth = 2)
    {
        if (string.IsNullOrWhiteSpace(entityName))
        {
            throw new ArgumentException("Entity name must not be null or whitespace.", nameof(entityName));
        }

        ArgumentNullException.ThrowIfNull(scopeId, nameof(scopeId));

        if (maxDepth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth), maxDepth, "MaxDepth must be a positive integer.");
        }

        EntityName = entityName;
        ScopeId = scopeId;
        MaxDepth = maxDepth;
    }
}
