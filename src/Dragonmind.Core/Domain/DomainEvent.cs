using MediatR;

namespace Dragonmind.Core.Domain;

/// <summary>
/// Marker interface for all domain events.
/// Domain events represent something that has happened in the domain that other parts of the system should be aware of.
/// Extends MediatR INotification for automatic handler discovery via assembly scanning.
/// </summary>
public interface IDomainEvent : INotification
{
    Guid EventId { get; }
    DateTime OccurredOn { get; }
}

/// <summary>
/// Base class for all domain events.
/// </summary>
public abstract class DomainEventBase : IDomainEvent
{
    public Guid EventId { get; } = Guid.NewGuid();
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
}
