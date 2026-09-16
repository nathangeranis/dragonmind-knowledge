using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Domain.Aggregates.KnowledgeGraphAggregate;

namespace Dragonmind.Knowledge.Domain.Repositories;

/// <summary>
/// Repository for managing knowledge facts in the graph database.
/// </summary>
public interface IKnowledgeGraphRepository
{
    /// <summary>
    /// Gets a knowledge fact by its ID.
    /// </summary>
    Task<KnowledgeFact?> GetByIdAsync(
        FactId id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all facts for a scope.
    /// </summary>
    Task<IReadOnlyList<KnowledgeFact>> GetByScopeIdAsync(
        ScopeId scopeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all facts where the given entity is the subject, scoped to <paramref name="scopeId"/>.
    /// </summary>
    /// <param name="entityName">The name of the subject entity.</param>
    /// <param name="scopeId">
    /// The scope whose facts to search. Required (no default): the graph is a single shared
    /// store keyed on one graph name, so two scopes naming the same entity would otherwise see
    /// each other's facts. Every relationship traversed by this query is constrained to this scope.
    /// </param>
    Task<IReadOnlyList<KnowledgeFact>> GetFactsBySubjectAsync(
        string entityName,
        ScopeId scopeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all facts where the given entity is the object, scoped to <paramref name="scopeId"/>.
    /// </summary>
    /// <param name="entityName">The name of the object entity.</param>
    /// <param name="scopeId">
    /// The scope whose facts to search. See <see cref="GetFactsBySubjectAsync"/> for why this is
    /// required rather than defaulted.
    /// </param>
    Task<IReadOnlyList<KnowledgeFact>> GetFactsByObjectAsync(
        string entityName,
        ScopeId scopeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all facts related to an entity (as subject or object), scoped to <paramref name="scopeId"/>.
    /// </summary>
    /// <param name="entityName">The name of the entity.</param>
    /// <param name="scopeId">
    /// The scope whose facts to search. See <see cref="GetFactsBySubjectAsync"/> for why this is
    /// required rather than defaulted.
    /// </param>
    Task<IReadOnlyList<KnowledgeFact>> GetFactsByEntityAsync(
        string entityName,
        ScopeId scopeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a new knowledge fact to the graph.
    /// </summary>
    Task AddAsync(
        KnowledgeFact fact,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing knowledge fact.
    /// </summary>
    Task UpdateAsync(
        KnowledgeFact fact,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a knowledge fact.
    /// </summary>
    Task DeleteAsync(
        FactId id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all facts within N hops of the given entity, scoped to <paramref name="scopeId"/>.
    /// </summary>
    /// <param name="entityName">The name of the entity to start from.</param>
    /// <param name="scopeId">
    /// The scope whose facts to search. See <see cref="GetFactsBySubjectAsync"/> for why this is
    /// required rather than defaulted.
    /// </param>
    /// <param name="maxDistance">Maximum number of hops to traverse.</param>
    Task<IReadOnlyList<(KnowledgeFact Fact, int Distance)>> GetFactsWithinDistanceAsync(
        string entityName,
        ScopeId scopeId,
        int maxDistance = 2,
        CancellationToken cancellationToken = default);
}
