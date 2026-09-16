using System.Collections.Concurrent;
using System.Diagnostics;

using MediatR;

using Microsoft.Extensions.Logging;

namespace Dragonmind.Core.Application.Behaviors;

/// <summary>
/// MediatR pipeline behavior that logs request execution with timing.
/// Logs start/end of every request and warns on handlers slower than SlowRequestThresholdMs.
/// </summary>
public sealed class LoggingPipelineBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly ILogger<LoggingPipelineBehavior<TRequest, TResponse>> _logger;
    private const int SlowRequestThresholdMs = 1500;

    // Cache the computed request type classification to avoid repeated reflection on hot paths
    private static readonly ConcurrentDictionary<Type, string> RequestTypeCache = new();

    public LoggingPipelineBehavior(ILogger<LoggingPipelineBehavior<TRequest, TResponse>> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;

        // Determine if this is a Command or Query by checking the type's interfaces.
        // We check the type itself since pattern matching on generic covariant interfaces
        // (ICommand<out TResult>) is unreliable when TResult doesn't match TResponse.
        // Cache the result per request type to avoid repeated reflection.
        var requestType = RequestTypeCache.GetOrAdd(typeof(TRequest), type =>
        {
            return type.GetInterfaces()
                .Any(i => i == typeof(ICommand) ||
                          (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>)))
                ? "Command" : "Query";
        });

        _logger.LogDebug("Handling {RequestType} {RequestName}", requestType, requestName);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var response = await next();
            stopwatch.Stop();

            if (stopwatch.ElapsedMilliseconds > SlowRequestThresholdMs)
            {
                _logger.LogWarning(
                    "SLOW HANDLER: {RequestType} {RequestName} completed in {ElapsedMs}ms (threshold: {Threshold}ms)",
                    requestType, requestName, stopwatch.ElapsedMilliseconds, SlowRequestThresholdMs);
            }
            else
            {
                _logger.LogDebug(
                    "Handled {RequestType} {RequestName} in {ElapsedMs}ms",
                    requestType, requestName, stopwatch.ElapsedMilliseconds);
            }

            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(
                ex,
                "FAILED {RequestType} {RequestName} after {ElapsedMs}ms: {ErrorMessage}",
                requestType, requestName, stopwatch.ElapsedMilliseconds, ex.Message);
            throw;
        }
    }
}
