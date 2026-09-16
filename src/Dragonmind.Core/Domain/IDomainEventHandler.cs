using MediatR;

namespace Dragonmind.Core.Domain;

/// <summary>
/// Marker interface for domain event handlers.
/// Used for service registration and handler discovery.
/// </summary>
public interface IDomainEventHandler
{
}

/// <summary>
/// Bridge interface for domain event handlers.
/// Extends MediatR's INotificationHandler for automatic discovery via assembly scanning,
/// while preserving the existing HandleAsync method signature for backward compatibility.
/// Mirrors the IIntegrationEventHandler&lt;T&gt; pattern used for integration events.
/// </summary>
/// <typeparam name="TEvent">The type of domain event this handler processes</typeparam>
public interface IDomainEventHandler<in TEvent> : IDomainEventHandler, INotificationHandler<TEvent>
    where TEvent : IDomainEvent
{
    /// <summary>
    /// Handles the domain event asynchronously.
    /// </summary>
    /// <param name="domainEvent">The domain event to handle</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bridge to MediatR's Handle method via default interface implementation.
    /// </summary>
    Task INotificationHandler<TEvent>.Handle(TEvent notification, CancellationToken cancellationToken)
        => HandleAsync(notification, cancellationToken);
}
