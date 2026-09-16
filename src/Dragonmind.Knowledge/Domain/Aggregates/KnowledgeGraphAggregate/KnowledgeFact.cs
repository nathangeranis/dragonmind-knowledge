using Dragonmind.Knowledge.Domain.DomainEvents;
using Dragonmind.Core.Domain;
using Dragonmind.Core.Domain.SharedIdentities;

namespace Dragonmind.Knowledge.Domain.Aggregates.KnowledgeGraphAggregate;

/// <summary>
/// Aggregate root representing a fact in the knowledge graph.
/// Represents a triple: Subject-Predicate-Object (e.g., "Checkout Service" "depends on" "Payments DB").
/// </summary>
public sealed class KnowledgeFact : AggregateRoot<FactId>
{
    private KnowledgeFact(
        FactId id,
        ScopeId scopeId,
        GraphEntity subject,
        string predicate,
        GraphEntity @object,
        DateTime timestamp)
    {
        Id = id;
        ScopeId = scopeId;
        Subject = subject;
        Predicate = predicate;
        Object = @object;
        Timestamp = timestamp;
    }

    /// <summary>
    /// The scope this fact was learned in.
    /// </summary>
    public ScopeId ScopeId { get; private set; }

    /// <summary>
    /// The subject entity of the fact.
    /// </summary>
    public GraphEntity Subject { get; private set; }

    /// <summary>
    /// The predicate describing the relationship.
    /// </summary>
    public string Predicate { get; private set; }

    /// <summary>
    /// The object entity of the fact.
    /// </summary>
    public GraphEntity Object { get; private set; }

    /// <summary>
    /// When this fact was learned.
    /// </summary>
    public DateTime Timestamp { get; private set; }

    /// <summary>
    /// Creates a new knowledge fact (triple).
    /// </summary>
    public static KnowledgeFact Create(
        ScopeId scopeId,
        GraphEntity subject,
        string predicate,
        GraphEntity @object)
    {
        ArgumentNullException.ThrowIfNull(scopeId, nameof(scopeId));
        ArgumentNullException.ThrowIfNull(subject, nameof(subject));
        ArgumentException.ThrowIfNullOrWhiteSpace(predicate, nameof(predicate));
        ArgumentNullException.ThrowIfNull(@object, nameof(@object));

        var factId = FactId.New();
        var timestamp = DateTime.UtcNow;

        var fact = new KnowledgeFact(
            factId,
            scopeId,
            subject,
            predicate.Trim(),
            @object,
            timestamp);

        fact.AddDomainEvent(new KnowledgeFactCreatedDomainEvent(
            factId,
            scopeId,
            subject.Name,
            predicate.Trim(),
            @object.Name,
            timestamp));

        return fact;
    }

    /// <summary>
    /// Creates a fact from string parameters (convenience method).
    /// </summary>
    public static KnowledgeFact CreateFromStrings(
        ScopeId scopeId,
        string subjectName,
        string subjectType,
        string predicate,
        string objectName,
        string objectType)
    {
        var subject = GraphEntity.Create(subjectName, subjectType);
        var @object = GraphEntity.Create(objectName, objectType);

        return Create(scopeId, subject, predicate, @object);
    }

    /// <summary>
    /// Reconstitutes a knowledge fact from repository data.
    /// This factory method is used by repositories to recreate aggregates from persistence.
    /// Does not raise domain events as this represents an existing fact.
    /// </summary>
    public static KnowledgeFact Reconstitute(
        FactId id,
        ScopeId scopeId,
        GraphEntity subject,
        string predicate,
        GraphEntity @object,
        DateTime timestamp)
    {
        ArgumentNullException.ThrowIfNull(id, nameof(id));
        ArgumentNullException.ThrowIfNull(scopeId, nameof(scopeId));
        ArgumentNullException.ThrowIfNull(subject, nameof(subject));
        ArgumentException.ThrowIfNullOrWhiteSpace(predicate, nameof(predicate));
        ArgumentNullException.ThrowIfNull(@object, nameof(@object));

        return new KnowledgeFact(id, scopeId, subject, predicate, @object, timestamp);
    }

    public override string ToString() => $"{Subject.Name} {Predicate} {Object.Name}";
}
