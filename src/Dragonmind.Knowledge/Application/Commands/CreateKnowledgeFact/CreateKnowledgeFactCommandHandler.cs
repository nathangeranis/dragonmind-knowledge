using Microsoft.Extensions.Logging;
using Dragonmind.Core.Application;
using Dragonmind.Core.Application.AntiCorruptionLayer.DTOs;
using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Domain.Repositories;
using Dragonmind.Knowledge.Domain.Aggregates.KnowledgeGraphAggregate;
using Dragonmind.Knowledge.Domain.ValueObjects;

namespace Dragonmind.Knowledge.Application.Commands.CreateKnowledgeFact;

/// <summary>
/// Handler for creating a new knowledge fact in the knowledge graph.
/// </summary>
public sealed class CreateKnowledgeFactCommandHandler : ICommandHandler<CreateKnowledgeFactCommand, KnowledgeFactDto?>
{
    private readonly IKnowledgeGraphRepository _repository;
    private readonly ILogger<CreateKnowledgeFactCommandHandler> _logger;

    public CreateKnowledgeFactCommandHandler(
        IKnowledgeGraphRepository repository,
        ILogger<CreateKnowledgeFactCommandHandler> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<KnowledgeFactDto?> HandleAsync(
        CreateKnowledgeFactCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command, nameof(command));

        // The graph's relationship vocabulary is closed: Apache AGE cannot parameterize a
        // relationship type, so an unrecognized predicate is rejected here, before anything is
        // built or persisted, rather than left for the repository's own allowlist guard to catch.
        var normalizedPredicate = RelationshipTypes.Normalize(command.Predicate);
        if (!RelationshipTypes.IsAllowed(normalizedPredicate))
        {
            _logger.LogWarning(
                "Rejected knowledge fact with disallowed predicate {Predicate}",
                normalizedPredicate);
            return null;
        }

        // Create the knowledge fact from strings
        var scopeId = ScopeId.From(command.ScopeId);
        var fact = KnowledgeFact.CreateFromStrings(
            scopeId,
            command.SubjectName,
            command.SubjectType,
            normalizedPredicate,
            command.ObjectName,
            command.ObjectType);

        // Save to repository
        await _repository.AddAsync(fact, cancellationToken);

        // Return DTO using property initializer syntax
        return new KnowledgeFactDto
        {
            FactId = fact.Id.Value,
            ScopeId = fact.ScopeId,
            Subject = fact.Subject.Name,
            SubjectType = fact.Subject.EntityType,
            Predicate = fact.Predicate,
            Object = fact.Object.Name,
            ObjectType = fact.Object.EntityType,
            Timestamp = fact.Timestamp
        };
    }
}
