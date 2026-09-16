using Dragonmind.Knowledge.Application.Commands.AddKnowledgeDocument;
using Dragonmind.Knowledge.Application.Commands.CreateKnowledgeFact;
using Dragonmind.Knowledge.Application.Queries.SearchKnowledge;
using Dragonmind.Knowledge.Application.Queries.GetRelatedFacts;
using MediatR;
using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Core.Application.AntiCorruptionLayer;
using Dragonmind.Core.Application.AntiCorruptionLayer.DTOs;

namespace Dragonmind.Knowledge.Infrastructure.AntiCorruptionLayer;

/// <summary>
/// Anti-Corruption Layer facade for the Knowledge bounded context.
/// Provides a clean interface for other contexts to interact with the Knowledge system.
/// Uses MediatR to dispatch commands and queries to their handlers.
/// </summary>
public sealed class KnowledgeContextFacade : IKnowledgeContextFacade
{
    private readonly IMediator _mediator;

    public KnowledgeContextFacade(IMediator mediator)
    {
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
    }

    public async Task<IReadOnlyList<KnowledgeSnippetDto>> SearchKnowledgeAsync(
        string query,
        ScopeId? scopeId,
        int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        // Input validation is performed by SearchKnowledgeQuery constructor
        var searchQuery = new SearchKnowledgeQuery(query, maxResults, scopeId: scopeId?.Value);
        var results = await _mediator.Send(searchQuery, cancellationToken);

        return results
            .Select(r => new KnowledgeSnippetDto
            {
                DocumentId = r.Document.DocumentId,
                Content = r.Document.Content,
                RelevanceScore = r.SimilarityScore,
                Source = r.Document.Source ?? "Unknown"
            })
            .ToList();
    }

    public async Task<DocumentId> StoreKnowledgeAsync(
        string content,
        string source,
        ScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        var command = new AddKnowledgeDocumentCommand { ScopeId = scopeId.Value, Content = content, Source = source };
        var result = await _mediator.Send(command, cancellationToken);

        return DocumentId.From(result.DocumentId);
    }

    public async Task<IReadOnlyList<KnowledgeFactDto>> GetRelatedFactsAsync(
        string entityName,
        ScopeId scopeId,
        int maxDepth = 2,
        CancellationToken cancellationToken = default)
    {
        // Input validation is performed by GetRelatedFactsQuery constructor
        var query = new GetRelatedFactsQuery(entityName, scopeId, maxDepth);
        // Distance is set on the DTO by the handler, where the traversal result is mapped, so
        // there is nothing to fold in or rebuild here. This used to patch Distance onto the DTO
        // with a `with` expression, which quietly made the facade the only caller that saw a
        // correct value.
        return await _mediator.Send(query, cancellationToken);
    }

    public async Task<bool> AddKnowledgeFactAsync(
        string subject,
        string predicate,
        string @object,
        ScopeId scopeId,
        string subjectType = "Entity",
        string objectType = "Entity",
        CancellationToken cancellationToken = default)
    {
        var command = new CreateKnowledgeFactCommand
        {
            ScopeId = scopeId.Value,
            SubjectName = subject,
            SubjectType = subjectType,
            Predicate = predicate,
            ObjectName = @object,
            ObjectType = objectType
        };

        var result = await _mediator.Send(command, cancellationToken);
        return result is not null;
    }
}
