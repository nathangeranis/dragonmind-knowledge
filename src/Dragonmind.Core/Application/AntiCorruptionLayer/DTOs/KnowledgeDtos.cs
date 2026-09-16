namespace Dragonmind.Core.Application.AntiCorruptionLayer.DTOs;

/// <summary>
/// DTO for knowledge graph facts.
/// Represents a relationship between two entities in the knowledge graph.
/// </summary>
public sealed record KnowledgeFactDto
{
    /// <summary>
    /// The unique identifier of this fact.
    /// </summary>
    public Guid? FactId { get; init; }

    /// <summary>
    /// The scope this fact belongs to.
    /// </summary>
    public Guid? ScopeId { get; init; }

    /// <summary>
    /// The subject entity name.
    /// </summary>
    public required string Subject { get; init; }

    /// <summary>
    /// The type of the subject entity (e.g., "Person", "Location", "Item").
    /// </summary>
    public string? SubjectType { get; init; }

    /// <summary>
    /// The relationship/predicate between subject and object.
    /// </summary>
    public required string Predicate { get; init; }

    /// <summary>
    /// The object entity name.
    /// </summary>
    public required string Object { get; init; }

    /// <summary>
    /// The type of the object entity.
    /// </summary>
    public string? ObjectType { get; init; }

    /// <summary>
    /// Distance from the queried entity in graph traversal.
    /// Used for relevance scoring in graph queries.
    /// </summary>
    public int Distance { get; init; }

    /// <summary>
    /// When this fact was recorded.
    /// </summary>
    public DateTime? Timestamp { get; init; }
}

/// <summary>
/// DTO for knowledge snippet results from semantic search.
/// </summary>
public sealed record KnowledgeSnippetDto
{
    /// <summary>
    /// The document ID.
    /// </summary>
    public required Guid DocumentId { get; init; }

    /// <summary>
    /// The content of the knowledge snippet.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// Relevance score from semantic search (0.0 to 1.0).
    /// </summary>
    public required double RelevanceScore { get; init; }

    /// <summary>
    /// The source of this snippet (e.g., "Checkout Service runbook", "architecture-notes").
    /// </summary>
    public required string Source { get; init; }
}
