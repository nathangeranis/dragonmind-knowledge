using System.ComponentModel.DataAnnotations;

using MediatR;

using Microsoft.Extensions.Logging;

namespace Dragonmind.Core.Application.Behaviors;

/// <summary>
/// MediatR pipeline behavior that validates DataAnnotation attributes on requests.
/// Runs before the handler and throws ValidationException if any attributes fail.
/// Only validates requests that have DataAnnotation attributes (commands/queries).
/// </summary>
public sealed class ValidationPipelineBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly ILogger<ValidationPipelineBehavior<TRequest, TResponse>> _logger;

    public ValidationPipelineBehavior(ILogger<ValidationPipelineBehavior<TRequest, TResponse>> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var validationResults = new List<ValidationResult>();
        var validationContext = new ValidationContext(request);

        if (!Validator.TryValidateObject(request, validationContext, validationResults, validateAllProperties: true))
        {
            var requestName = typeof(TRequest).Name;
            var errors = string.Join("; ", validationResults.Select(r => r.ErrorMessage));

            _logger.LogWarning(
                "Validation failed for {RequestName}: {Errors}",
                requestName, errors);

            throw new ValidationException(
                $"Invalid request: {errors}");
        }

        return await next();
    }
}
