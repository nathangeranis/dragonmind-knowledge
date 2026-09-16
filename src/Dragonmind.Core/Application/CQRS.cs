using MediatR;

namespace Dragonmind.Core.Application;

/// <summary>
/// Marker interface for commands that don't return a result.
/// Inherits from MediatR's IRequest for automatic handler discovery and pipeline behaviors.
/// </summary>
public interface ICommand : IRequest<Unit> { }

/// <summary>
/// Marker interface for commands that return a result.
/// Inherits from MediatR's IRequest for automatic handler discovery and pipeline behaviors.
/// </summary>
public interface ICommand<out TResult> : IRequest<TResult> { }

/// <summary>
/// Marker interface for queries.
/// Inherits from MediatR's IRequest for automatic handler discovery and pipeline behaviors.
/// </summary>
public interface IQuery<out TResult> : IRequest<TResult> { }

/// <summary>
/// Handler for commands that don't return a result.
/// Bridges to MediatR's IRequestHandler via default interface implementation,
/// allowing existing HandleAsync methods to work with MediatR's Send pipeline.
/// </summary>
public interface ICommandHandler<in TCommand> : IRequestHandler<TCommand, Unit>
    where TCommand : ICommand
{
    Task HandleAsync(TCommand command, CancellationToken cancellationToken = default);

    async Task<Unit> IRequestHandler<TCommand, Unit>.Handle(TCommand request, CancellationToken cancellationToken)
    {
        await HandleAsync(request, cancellationToken);
        return Unit.Value;
    }
}

/// <summary>
/// Handler for commands that return a result.
/// Bridges to MediatR's IRequestHandler via default interface implementation.
/// </summary>
public interface ICommandHandler<in TCommand, TResult> : IRequestHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken = default);

    Task<TResult> IRequestHandler<TCommand, TResult>.Handle(TCommand request, CancellationToken cancellationToken)
        => HandleAsync(request, cancellationToken);
}

/// <summary>
/// Handler for queries.
/// Bridges to MediatR's IRequestHandler via default interface implementation.
/// </summary>
public interface IQueryHandler<in TQuery, TResult> : IRequestHandler<TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken = default);

    Task<TResult> IRequestHandler<TQuery, TResult>.Handle(TQuery request, CancellationToken cancellationToken)
        => HandleAsync(request, cancellationToken);
}
