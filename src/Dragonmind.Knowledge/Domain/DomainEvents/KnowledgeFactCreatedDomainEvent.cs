using Dragonmind.Core.Domain;
using Dragonmind.Core.Domain.SharedIdentities;

namespace Dragonmind.Knowledge.Domain.DomainEvents;

/// <summary>
/// Domain event raised when a new knowledge fact is created in the knowledge graph.
/// </summary>
public sealed class KnowledgeFactCreatedDomainEvent : DomainEventBase
{
    public KnowledgeFactCreatedDomainEvent(
        FactId factId,
        ScopeId scopeId,
        string subject,
        string predicate,
        string @object,
        DateTime timestamp)
    {
        FactId = factId;
        ScopeId = scopeId;
        Subject = subject;
        Predicate = predicate;
        Object = @object;
        Timestamp = timestamp;
    }

    /// <summary>
    /// The ID of the fact that was created.
    /// </summary>
    public FactId FactId { get; }

    /// <summary>
    /// The scope where the fact was learned.
    /// </summary>
    public ScopeId ScopeId { get; }

    /// <summary>
    /// The subject of the fact.
    /// </summary>
    public string Subject { get; }

    /// <summary>
    /// The predicate of the fact.
    /// </summary>
    public string Predicate { get; }

    /// <summary>
    /// The object of the fact.
    /// </summary>
    public string Object { get; }

    /// <summary>
    /// When the fact was learned.
    /// </summary>
    public DateTime Timestamp { get; }
}
