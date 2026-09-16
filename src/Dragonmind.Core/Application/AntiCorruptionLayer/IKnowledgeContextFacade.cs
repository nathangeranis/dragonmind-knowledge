using Dragonmind.Core.Application.AntiCorruptionLayer.DTOs;
using Dragonmind.Core.Domain.SharedIdentities;

namespace Dragonmind.Core.Application.AntiCorruptionLayer;

/// <summary>
/// Anti-Corruption Layer facade for the Knowledge bounded context.
/// Provides a clean interface for other contexts to interact with the Knowledge system without
/// touching its storage schema directly.
/// </summary>
public interface IKnowledgeContextFacade
{
    /// <summary>
    /// Performs a semantic search for relevant knowledge based on a query.
    /// Uses vector similarity search (RAG).
    /// </summary>
    /// <param name="query">The natural-language query to search for.</param>
    /// <param name="scopeId">
    /// The scope whose knowledge to search, or <c>null</c> to search EVERY scope's knowledge.
    /// Deliberately required rather than defaulted: results are ranked purely by cosine distance
    /// with no scope predicate, so an omitted id silently lets every other scope compete for the
    /// same <paramref name="maxResults"/> slots — a failure that produces no error and no log line.
    /// Making it explicit turns forgetting it into a compile error instead.
    /// </param>
    /// <param name="maxResults">Maximum number of results to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<KnowledgeSnippetDto>> SearchKnowledgeAsync(
        string query,
        ScopeId? scopeId,
        int maxResults = 5,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a new knowledge document with its embedding.
    /// </summary>
    /// <param name="content">The document's text content.</param>
    /// <param name="source">Where this document came from (e.g. "Checkout Service runbook").</param>
    /// <param name="scopeId">The scope this document belongs to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DocumentId> StoreKnowledgeAsync(
        string content,
        string source,
        ScopeId scopeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries the knowledge graph for related entities.
    /// </summary>
    /// <param name="entityName">The name of the entity to start from.</param>
    /// <param name="scopeId">
    /// The scope whose facts to search. Deliberately required rather than defaulted, mirroring
    /// <see cref="SearchKnowledgeAsync"/>: the knowledge graph is a single shared store keyed on
    /// one graph name, not partitioned per scope, so every traversal is constrained by a scope
    /// predicate on each relationship it follows rather than by table/schema isolation. An omitted
    /// id would silently let another scope's facts about an identically named entity leak into this
    /// scope's context — a failure that produces no error and no log line. Making it explicit turns
    /// forgetting it into a compile error instead.
    /// </param>
    /// <param name="maxDepth">Maximum depth to traverse (default: 2).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<KnowledgeFactDto>> GetRelatedFactsAsync(
        string entityName,
        ScopeId scopeId,
        int maxDepth = 2,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a fact to the knowledge graph.
    /// </summary>
    /// <param name="subject">The subject entity name.</param>
    /// <param name="predicate">The relationship/predicate between subject and object.</param>
    /// <param name="object">The object entity name.</param>
    /// <param name="scopeId">The scope this fact belongs to.</param>
    /// <param name="subjectType">The type of the subject entity (e.g., "Service", "Team", "Component"). Defaults to "Entity".</param>
    /// <param name="objectType">The type of the object entity. Defaults to "Entity".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>true</c> if the fact was written; <c>false</c> if <paramref name="predicate"/> is not one
    /// of the allowed relationship types, in which case nothing is written.
    /// </returns>
    Task<bool> AddKnowledgeFactAsync(
        string subject,
        string predicate,
        string @object,
        ScopeId scopeId,
        string subjectType = "Entity",
        string objectType = "Entity",
        CancellationToken cancellationToken = default);
}
