namespace Dragonmind.Knowledge.Application.DTOs;

/// <summary>
/// DTO for knowledge document data.
/// Note: DTOs are intentionally permissive for transport purposes.
/// Domain validation occurs when creating domain objects from DTOs.
/// </summary>
public class KnowledgeDocumentDto
{
    public KnowledgeDocumentDto(
        Guid documentId,
        Guid scopeId,
        string content,
        string? source,
        string? category,
        DateTime timestamp,
        bool hasEmbedding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content, nameof(content));

        DocumentId = documentId;
        ScopeId = scopeId;
        Content = content;
        Source = source;
        Category = category;
        Timestamp = timestamp;
        HasEmbedding = hasEmbedding;
    }

    public Guid DocumentId { get; }
    public Guid ScopeId { get; }
    public string Content { get; }
    public string? Source { get; }
    public string? Category { get; }
    public DateTime Timestamp { get; }
    public bool HasEmbedding { get; }
}
