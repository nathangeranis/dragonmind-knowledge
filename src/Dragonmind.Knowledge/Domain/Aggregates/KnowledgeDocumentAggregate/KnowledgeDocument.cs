using Dragonmind.Knowledge.Domain.DomainEvents;
using Dragonmind.Core.Domain;
using Dragonmind.Core.Domain.SharedIdentities;

namespace Dragonmind.Knowledge.Domain.Aggregates.KnowledgeDocumentAggregate;

/// <summary>
/// Aggregate root representing a document of knowledge with its vector embedding.
/// Used for semantic search (RAG - Retrieval-Augmented Generation).
/// </summary>
public sealed class KnowledgeDocument : AggregateRoot<DocumentId>
{
    // Parameterless constructor for EF Core materialization.
    // Uses null-forgiving assignments because EF Core overwrites all properties
    // immediately after construction. Domain validation in value object factories
    // (DocumentContent.Create, ScopeId.From) would throw on placeholder values.
    private KnowledgeDocument()
    {
        Id = DocumentId.New();
        ScopeId = ScopeId.New();
        Content = null!; // EF Core will set this from the DB row
        Embedding = null;
        Timestamp = DateTime.MinValue;
    }

    private KnowledgeDocument(
        DocumentId id,
        ScopeId scopeId,
        DocumentContent content,
        Embedding? embedding,
        DateTime timestamp)
    {
        Id = id;
        ScopeId = scopeId;
        Content = content;
        Embedding = embedding;
        Timestamp = timestamp;
    }

    /// <summary>
    /// The scope this document is associated with.
    /// </summary>
    public ScopeId ScopeId { get; private set; }

    /// <summary>
    /// The content of the document.
    /// </summary>
    public DocumentContent Content { get; private set; }

    /// <summary>
    /// The vector embedding of the content for semantic search.
    /// May be null if embedding hasn't been generated yet.
    /// </summary>
    public Embedding? Embedding { get; private set; }

    /// <summary>
    /// When this document was created.
    /// </summary>
    public DateTime Timestamp { get; private set; }

    /// <summary>
    /// Creates a new knowledge document without an embedding.
    /// The embedding should be set later via <see cref="SetEmbedding"/>.
    /// </summary>
    public static KnowledgeDocument Create(
        ScopeId scopeId,
        DocumentContent content)
    {
        ArgumentNullException.ThrowIfNull(scopeId, nameof(scopeId));
        ArgumentNullException.ThrowIfNull(content, nameof(content));

        var documentId = DocumentId.New();
        var timestamp = DateTime.UtcNow;

        var document = new KnowledgeDocument(
            documentId,
            scopeId,
            content,
            embedding: null,
            timestamp);

        document.AddDomainEvent(new KnowledgeDocumentAddedDomainEvent(
            documentId,
            scopeId,
            content.Value,
            timestamp));

        return document;
    }

    /// <summary>
    /// Creates a new knowledge document with a pre-computed embedding.
    /// </summary>
    public static KnowledgeDocument CreateWithEmbedding(
        ScopeId scopeId,
        DocumentContent content,
        Embedding embedding)
    {
        ArgumentNullException.ThrowIfNull(scopeId, nameof(scopeId));
        ArgumentNullException.ThrowIfNull(content, nameof(content));
        ArgumentNullException.ThrowIfNull(embedding, nameof(embedding));

        var documentId = DocumentId.New();
        var timestamp = DateTime.UtcNow;

        var document = new KnowledgeDocument(
            documentId,
            scopeId,
            content,
            embedding,
            timestamp);

        document.AddDomainEvent(new KnowledgeDocumentAddedDomainEvent(
            documentId,
            scopeId,
            content.Value,
            timestamp));

        return document;
    }

    /// <summary>
    /// Reconstitutes a knowledge document from repository data.
    /// This factory method is used by repositories to recreate aggregates from persistence.
    /// Does not raise domain events as this represents an existing document.
    /// </summary>
    public static KnowledgeDocument Reconstitute(
        DocumentId id,
        ScopeId scopeId,
        DocumentContent content,
        Embedding? embedding,
        DateTime timestamp)
    {
        ArgumentNullException.ThrowIfNull(id, nameof(id));
        ArgumentNullException.ThrowIfNull(scopeId, nameof(scopeId));
        ArgumentNullException.ThrowIfNull(content, nameof(content));

        return new KnowledgeDocument(id, scopeId, content, embedding, timestamp);
    }

    /// <summary>
    /// Sets or updates the embedding for this document.
    /// </summary>
    public void SetEmbedding(Embedding embedding)
    {
        ArgumentNullException.ThrowIfNull(embedding, nameof(embedding));

        Embedding = embedding;
    }

    /// <summary>
    /// Updates the document content and clears the embedding.
    /// A new embedding should be generated after content changes.
    /// </summary>
    public void UpdateContent(DocumentContent newContent)
    {
        ArgumentNullException.ThrowIfNull(newContent, nameof(newContent));

        Content = newContent;
        Embedding = null; // Clear embedding when content changes
    }

}
