using Dragonmind.Core.Application.AntiCorruptionLayer.DTOs;
using Dragonmind.Knowledge.Application.Commands.CreateKnowledgeFact;
using Dragonmind.Knowledge.Domain.Aggregates.KnowledgeGraphAggregate;
using Dragonmind.Knowledge.Domain.DomainEvents;
using Dragonmind.Knowledge.Domain.Repositories;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace Dragonmind.Knowledge.UnitTests.Application;

/// <summary>
/// Covers <see cref="CreateKnowledgeFactCommandHandler"/>: the relationship-type check that must
/// run before anything is built or persisted, the DTO mapping of a successfully persisted fact,
/// the domain event it raises, and that the caller's cancellation token reaches the repository.
/// </summary>
public class CreateKnowledgeFactCommandHandlerTests
{
    private static (CreateKnowledgeFactCommandHandler Handler, Mock<IKnowledgeGraphRepository> Repository) Harness()
    {
        var repository = new Mock<IKnowledgeGraphRepository>();
        var handler = new CreateKnowledgeFactCommandHandler(repository.Object, NullLogger<CreateKnowledgeFactCommandHandler>.Instance);
        return (handler, repository);
    }

    private static CreateKnowledgeFactCommand BuildCommand(string predicate = "DEPENDS_ON") => new()
    {
        ScopeId = Guid.NewGuid(),
        SubjectName = "Checkout Service",
        SubjectType = "Service",
        Predicate = predicate,
        ObjectName = "Payments DB",
        ObjectType = "Service"
    };

    [Fact]
    public async Task HandleAsync_NullCommand_ThrowsArgumentNullException()
    {
        var (handler, _) = Harness();

        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.HandleAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_UnknownPredicate_WritesNothingAndReturnsNull()
    {
        var (handler, repository) = Harness();

        var result = await handler.HandleAsync(BuildCommand("NOT_A_REAL_RELATIONSHIP"), CancellationToken.None);

        Assert.Null(result);
        repository.Verify(r => r.AddAsync(It.IsAny<KnowledgeFact>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleAsync_AllowedPredicate_PersistsExactlyOneFact()
    {
        var (handler, repository) = Harness();

        repository
            .Setup(r => r.AddAsync(It.IsAny<KnowledgeFact>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await handler.HandleAsync(BuildCommand(), CancellationToken.None);

        repository.Verify(r => r.AddAsync(It.IsAny<KnowledgeFact>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HandleAsync_AllowedPredicate_ReturnsADtoCopyingEveryField()
    {
        var (handler, repository) = Harness();
        var command = BuildCommand();

        repository
            .Setup(r => r.AddAsync(It.IsAny<KnowledgeFact>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotNull(result!.FactId);
        Assert.NotEqual(Guid.Empty, result.FactId!.Value);
        Assert.Equal(command.ScopeId, result.ScopeId);
        Assert.Equal(command.SubjectName, result.Subject);
        Assert.Equal(command.SubjectType, result.SubjectType);
        Assert.Equal(command.Predicate, result.Predicate);
        Assert.Equal(command.ObjectName, result.Object);
        Assert.Equal(command.ObjectType, result.ObjectType);
        Assert.NotNull(result.Timestamp);
        Assert.True(result.Timestamp <= DateTime.UtcNow);
    }

    [Fact]
    public async Task HandleAsync_AllowedPredicate_PersistsAFactThatRaisedKnowledgeFactCreatedDomainEvent()
    {
        var (handler, repository) = Harness();
        var command = BuildCommand();
        KnowledgeFact? persisted = null;

        repository
            .Setup(r => r.AddAsync(It.IsAny<KnowledgeFact>(), It.IsAny<CancellationToken>()))
            .Callback<KnowledgeFact, CancellationToken>((fact, _) => persisted = fact)
            .Returns(Task.CompletedTask);

        await handler.HandleAsync(command, CancellationToken.None);

        Assert.NotNull(persisted);
        var domainEvent = Assert.Single(persisted!.DomainEvents);
        var factCreated = Assert.IsType<KnowledgeFactCreatedDomainEvent>(domainEvent);
        Assert.Equal(persisted.Id, factCreated.FactId);
        Assert.Equal(command.SubjectName, factCreated.Subject);
        Assert.Equal(command.Predicate, factCreated.Predicate);
        Assert.Equal(command.ObjectName, factCreated.Object);
    }

    [Fact]
    public async Task HandleAsync_ForwardsTheCallersCancellationTokenToTheRepository()
    {
        var (handler, repository) = Harness();

        repository
            .Setup(r => r.AddAsync(It.IsAny<KnowledgeFact>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        using var cts = new CancellationTokenSource();

        await handler.HandleAsync(BuildCommand(), cts.Token);

        repository.Verify(r => r.AddAsync(It.IsAny<KnowledgeFact>(), cts.Token), Times.Once);
    }
}
