using Dragonmind.Core.Application;
using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Domain.DomainServices;
using Dragonmind.Knowledge.Domain.Repositories;
using Dragonmind.Knowledge.Domain.Aggregates.KnowledgeDocumentAggregate;
using Dragonmind.Knowledge.Domain.ValueObjects;
using Dragonmind.Knowledge.Application.DTOs;

namespace Dragonmind.Knowledge.Application.Commands.AddKnowledgeDocument;

/// <summary>
/// Handler for adding a new knowledge document to the knowledge base.
/// Generates an embedding for the document and stores it.
/// </summary>
public sealed class AddKnowledgeDocumentCommandHandler : ICommandHandler<AddKnowledgeDocumentCommand, KnowledgeDocumentDto>
{
    private readonly IKnowledgeDocumentRepository _repository;
    private readonly IEmbeddingService _embeddingService;

    public AddKnowledgeDocumentCommandHandler(
        IKnowledgeDocumentRepository repository,
        IEmbeddingService embeddingService)
    {
        _repository = repository;
        _embeddingService = embeddingService;
    }

    public async Task<KnowledgeDocumentDto> HandleAsync(
        AddKnowledgeDocumentCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command, nameof(command));

        // Create value objects
        var scopeId = ScopeId.From(command.ScopeId);
        var content = DocumentContent.Create(
            command.Content,
            command.Source,
            command.Category);

        // Generate embedding for the content
        var embedding = await _embeddingService.GenerateEmbeddingAsync(
            command.Content,
            cancellationToken);

        // Create the document with embedding
        var document = KnowledgeDocument.CreateWithEmbedding(scopeId, content, embedding);

        // Save to repository
        await _repository.AddAsync(document, cancellationToken);

        // Return DTO
        return new KnowledgeDocumentDto(
            document.Id,
            document.ScopeId,
            document.Content.Value,
            document.Content.Source,
            document.Content.Category,
            document.Timestamp,
            document.Embedding != null);
    }
}
