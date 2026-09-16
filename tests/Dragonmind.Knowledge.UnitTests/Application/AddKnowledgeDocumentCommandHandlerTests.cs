using Dragonmind.Knowledge.Application.Commands.AddKnowledgeDocument;
using Dragonmind.Knowledge.Application.DTOs;
using Dragonmind.Knowledge.Domain.Aggregates.KnowledgeDocumentAggregate;
using Dragonmind.Knowledge.Domain.DomainServices;
using Dragonmind.Knowledge.Domain.Repositories;
using Dragonmind.Knowledge.Domain.ValueObjects;

using Moq;

namespace Dragonmind.Knowledge.UnitTests.Application;

/// <summary>
/// Covers <see cref="AddKnowledgeDocumentCommandHandler"/>: it must embed the content before
/// persisting, persist a document that actually carries that embedding and the command's fields,
/// map every field onto the returned DTO, let a repository failure propagate unmasked, and thread
/// the caller's cancellation token to both collaborators unchanged.
/// </summary>
public class AddKnowledgeDocumentCommandHandlerTests
{
    private static Embedding SampleEmbedding() => Embedding.Create(new float[] { 0.1f, 0.2f, 0.3f });

    private static (AddKnowledgeDocumentCommandHandler Handler, Mock<IKnowledgeDocumentRepository> Repository, Mock<IEmbeddingService> EmbeddingService) Harness()
    {
        var repository = new Mock<IKnowledgeDocumentRepository>();
        var embeddingService = new Mock<IEmbeddingService>();
        var handler = new AddKnowledgeDocumentCommandHandler(repository.Object, embeddingService.Object);
        return (handler, repository, embeddingService);
    }

    private static AddKnowledgeDocumentCommand BuildCommand(string content = "Checkout retries three times.") => new()
    {
        ScopeId = Guid.NewGuid(),
        Content = content,
        Source = "runbook",
        Category = "Service"
    };

    [Fact]
    public async Task HandleAsync_NullCommand_ThrowsArgumentNullException()
    {
        var (handler, _, _) = Harness();

        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.HandleAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_EmbedsTheContentBeforePersistingTheDocument()
    {
        var (handler, repository, embeddingService) = Harness();
        var callOrder = new List<string>();

        embeddingService
            .Setup(s => s.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("embed"))
            .ReturnsAsync(SampleEmbedding());

        repository
            .Setup(r => r.AddAsync(It.IsAny<KnowledgeDocument>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("persist"))
            .Returns(Task.CompletedTask);

        await handler.HandleAsync(BuildCommand(), CancellationToken.None);

        Assert.Equal(new[] { "embed", "persist" }, callOrder);
    }

    [Fact]
    public async Task HandleAsync_PersistsADocumentCarryingTheGeneratedEmbeddingAndCommandFields()
    {
        var (handler, repository, embeddingService) = Harness();
        var embedding = SampleEmbedding();
        var command = BuildCommand("Inventory reconciles nightly.");

        embeddingService
            .Setup(s => s.GenerateEmbeddingAsync(command.Content, It.IsAny<CancellationToken>()))
            .ReturnsAsync(embedding);

        KnowledgeDocument? persisted = null;
        repository
            .Setup(r => r.AddAsync(It.IsAny<KnowledgeDocument>(), It.IsAny<CancellationToken>()))
            .Callback<KnowledgeDocument, CancellationToken>((doc, _) => persisted = doc)
            .Returns(Task.CompletedTask);

        await handler.HandleAsync(command, CancellationToken.None);

        Assert.NotNull(persisted);
        Assert.Equal(command.ScopeId, persisted!.ScopeId.Value);
        Assert.Equal(command.Content, persisted.Content.Value);
        Assert.Equal(command.Source, persisted.Content.Source);
        Assert.Equal(command.Category, persisted.Content.Category);
        Assert.Same(embedding, persisted.Embedding);
    }

    [Fact]
    public async Task HandleAsync_ReturnsADtoMirroringEveryDocumentField()
    {
        var (handler, repository, embeddingService) = Harness();
        var embedding = SampleEmbedding();
        var command = BuildCommand("Payments retries on timeout.");

        embeddingService
            .Setup(s => s.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(embedding);
        repository
            .Setup(r => r.AddAsync(It.IsAny<KnowledgeDocument>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.Equal(command.ScopeId, result.ScopeId);
        Assert.Equal(command.Content, result.Content);
        Assert.Equal(command.Source, result.Source);
        Assert.Equal(command.Category, result.Category);
        Assert.True(result.HasEmbedding);
        Assert.NotEqual(Guid.Empty, result.DocumentId);
        Assert.True(result.Timestamp <= DateTime.UtcNow);
    }

    [Fact]
    public async Task HandleAsync_RepositoryFailure_Propagates()
    {
        var (handler, repository, embeddingService) = Harness();

        embeddingService
            .Setup(s => s.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleEmbedding());
        repository
            .Setup(r => r.AddAsync(It.IsAny<KnowledgeDocument>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Storage unavailable"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(BuildCommand(), CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_ForwardsTheCallersCancellationTokenToBothCollaborators()
    {
        var (handler, repository, embeddingService) = Harness();

        embeddingService
            .Setup(s => s.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleEmbedding());
        repository
            .Setup(r => r.AddAsync(It.IsAny<KnowledgeDocument>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        using var cts = new CancellationTokenSource();

        await handler.HandleAsync(BuildCommand(), cts.Token);

        embeddingService.Verify(s => s.GenerateEmbeddingAsync(It.IsAny<string>(), cts.Token), Times.Once);
        repository.Verify(r => r.AddAsync(It.IsAny<KnowledgeDocument>(), cts.Token), Times.Once);
    }
}
