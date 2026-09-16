using Dragonmind.Core.Domain;
using Dragonmind.Core.Domain.SharedIdentities;

namespace Dragonmind.Knowledge.Domain.DomainEvents;

/// <summary>
/// Domain event raised when a new knowledge document is added to the knowledge base.
/// </summary>
public sealed class KnowledgeDocumentAddedDomainEvent : DomainEventBase
{
    public KnowledgeDocumentAddedDomainEvent(
        DocumentId documentId,
        ScopeId scopeId,
        string content,
        DateTime timestamp)
    {
        DocumentId = documentId;
        ScopeId = scopeId;
        Content = content;
        Timestamp = timestamp;
    }

    /// <summary>
    /// The ID of the document that was added.
    /// </summary>
    public DocumentId DocumentId { get; }

    /// <summary>
    /// The scope where the document was created.
    /// </summary>
    public ScopeId ScopeId { get; }

    /// <summary>
    /// The content of the document.
    /// </summary>
    public string Content { get; }

    /// <summary>
    /// When the document was added.
    /// </summary>
    public DateTime Timestamp { get; }
}
