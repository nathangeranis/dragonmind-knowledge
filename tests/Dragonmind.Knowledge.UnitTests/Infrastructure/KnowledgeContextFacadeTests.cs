using Dragonmind.Core.Application.AntiCorruptionLayer.DTOs;
using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Application.Commands.AddKnowledgeDocument;
using Dragonmind.Knowledge.Application.Commands.CreateKnowledgeFact;
using Dragonmind.Knowledge.Application.DTOs;
using Dragonmind.Knowledge.Application.Queries.GetRelatedFacts;
using Dragonmind.Knowledge.Application.Queries.SearchKnowledge;
using Dragonmind.Knowledge.Infrastructure.AntiCorruptionLayer;

using MediatR;

using Moq;

namespace Dragonmind.Knowledge.UnitTests.Infrastructure;

/// <summary>
/// Covers every member of <see cref="KnowledgeContextFacade"/>: the command/query it constructs
/// from its parameters, the DTO mapping it performs on the handler's result, and that the
/// caller's scope and cancellation token are threaded through unchanged.
/// <para>
/// <see cref="GetRelatedFactsAsync"/> in particular guards against a facade that reconstructs a
/// new <see cref="KnowledgeFactDto"/> copying only the five text/type fields and silently
/// discards identity and recency information (FactId, ScopeId, Timestamp) or hard-codes Distance.
/// </para>
/// </summary>
public class KnowledgeContextFacadeTests
{
    private static (KnowledgeContextFacade Facade, Mock<IMediator> Mediator) BuildFacade()
    {
        var mediator = new Mock<IMediator>();
        return (new KnowledgeContextFacade(mediator.Object), mediator);
    }

    [Fact]
    public void Constructor_NullMediator_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new KnowledgeContextFacade(null!));
    }

    #region SearchKnowledgeAsync

    [Fact]
    public async Task SearchKnowledgeAsync_MapsHandlerResultsToSnippetDtos()
    {
        var (facade, mediator) = BuildFacade();

        var documentId = Guid.NewGuid();
        var handlerResult = new List<VectorSearchResultDto>
        {
            new(
                new KnowledgeDocumentDto(documentId, Guid.NewGuid(), "Checkout retries three times.", "runbook", null, DateTime.UtcNow, hasEmbedding: true),
                0.87)
        };

        mediator
            .Setup(m => m.Send(It.IsAny<SearchKnowledgeQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<VectorSearchResultDto>)handlerResult);

        var result = await facade.SearchKnowledgeAsync("checkout retries", ScopeId.New(), maxResults: 5);

        var snippet = Assert.Single(result);
        Assert.Equal(documentId, snippet.DocumentId);
        Assert.Equal("Checkout retries three times.", snippet.Content);
        Assert.Equal(0.87, snippet.RelevanceScore);
        Assert.Equal("runbook", snippet.Source);
    }

    [Fact]
    public async Task SearchKnowledgeAsync_MissingSource_DefaultsToUnknown()
    {
        var (facade, mediator) = BuildFacade();

        var handlerResult = new List<VectorSearchResultDto>
        {
            new(
                new KnowledgeDocumentDto(Guid.NewGuid(), Guid.NewGuid(), "No source recorded.", null, null, DateTime.UtcNow, hasEmbedding: false),
                0.5)
        };

        mediator
            .Setup(m => m.Send(It.IsAny<SearchKnowledgeQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<VectorSearchResultDto>)handlerResult);

        var result = await facade.SearchKnowledgeAsync("anything", scopeId: null);

        Assert.Equal("Unknown", Assert.Single(result).Source);
    }

    [Fact]
    public async Task SearchKnowledgeAsync_ForwardsQueryAndMaxResults()
    {
        var (facade, mediator) = BuildFacade();
        SearchKnowledgeQuery? dispatched = null;

        mediator
            .Setup(m => m.Send(It.IsAny<SearchKnowledgeQuery>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<IReadOnlyList<VectorSearchResultDto>>, CancellationToken>((q, _) => dispatched = (SearchKnowledgeQuery)q)
            .ReturnsAsync((IReadOnlyList<VectorSearchResultDto>)new List<VectorSearchResultDto>());

        await facade.SearchKnowledgeAsync("checkout retries", ScopeId.New(), maxResults: 12);

        Assert.NotNull(dispatched);
        Assert.Equal("checkout retries", dispatched!.QueryText);
        Assert.Equal(12, dispatched.MaxResults);
    }

    [Fact]
    public async Task SearchKnowledgeAsync_ForwardsTheCallersCancellationToken()
    {
        var (facade, mediator) = BuildFacade();

        mediator
            .Setup(m => m.Send(It.IsAny<SearchKnowledgeQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<VectorSearchResultDto>)new List<VectorSearchResultDto>());

        using var cts = new CancellationTokenSource();

        await facade.SearchKnowledgeAsync("checkout retries", ScopeId.New(), cancellationToken: cts.Token);

        mediator.Verify(m => m.Send(It.IsAny<SearchKnowledgeQuery>(), cts.Token), Times.Once);
    }

    #endregion

    #region StoreKnowledgeAsync

    [Fact]
    public async Task StoreKnowledgeAsync_DispatchesCommand_LeavingCategoryNull()
    {
        var (facade, mediator) = BuildFacade();
        AddKnowledgeDocumentCommand? dispatched = null;

        mediator
            .Setup(m => m.Send(It.IsAny<AddKnowledgeDocumentCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<KnowledgeDocumentDto>, CancellationToken>((c, _) => dispatched = (AddKnowledgeDocumentCommand)c)
            .ReturnsAsync(new KnowledgeDocumentDto(Guid.NewGuid(), Guid.NewGuid(), "content", "runbook", null, DateTime.UtcNow, hasEmbedding: true));

        await facade.StoreKnowledgeAsync("content", "runbook", ScopeId.New());

        Assert.NotNull(dispatched);
        Assert.Equal("content", dispatched!.Content);
        Assert.Equal("runbook", dispatched.Source);
        // The facade's StoreKnowledgeAsync has no Category parameter — the command it builds
        // must leave Category unset rather than inventing a value.
        Assert.Null(dispatched.Category);
    }

    [Fact]
    public async Task StoreKnowledgeAsync_ReturnsTheDocumentIdFromTheHandlerResult()
    {
        var (facade, mediator) = BuildFacade();
        var documentId = Guid.NewGuid();

        mediator
            .Setup(m => m.Send(It.IsAny<AddKnowledgeDocumentCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KnowledgeDocumentDto(documentId, Guid.NewGuid(), "content", null, null, DateTime.UtcNow, hasEmbedding: true));

        var result = await facade.StoreKnowledgeAsync("content", "runbook", ScopeId.New());

        Assert.Equal(documentId, result.Value);
    }

    [Fact]
    public async Task StoreKnowledgeAsync_ForwardsTheCallersCancellationToken()
    {
        var (facade, mediator) = BuildFacade();

        mediator
            .Setup(m => m.Send(It.IsAny<AddKnowledgeDocumentCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KnowledgeDocumentDto(Guid.NewGuid(), Guid.NewGuid(), "content", null, null, DateTime.UtcNow, hasEmbedding: true));

        using var cts = new CancellationTokenSource();

        await facade.StoreKnowledgeAsync("content", "runbook", ScopeId.New(), cts.Token);

        mediator.Verify(m => m.Send(It.IsAny<AddKnowledgeDocumentCommand>(), cts.Token), Times.Once);
    }

    #endregion

    #region GetRelatedFactsAsync

    [Fact]
    public async Task GetRelatedFactsAsync_PreservesFactIdScopeTimestampAndDistance()
    {
        var (facade, mediator) = BuildFacade();

        var factId = Guid.NewGuid();
        var scopeId = Guid.NewGuid();
        var timestamp = new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

        var fact = new KnowledgeFactDto
        {
            FactId = factId,
            ScopeId = scopeId,
            Subject = "Checkout Service",
            SubjectType = "Service",
            Predicate = "DEPENDS_ON",
            Object = "Payments DB",
            ObjectType = "Service",
            Timestamp = timestamp,
            Distance = 3
        };

        var handlerResult = new List<KnowledgeFactDto> { fact };

        mediator
            .Setup(m => m.Send(It.IsAny<GetRelatedFactsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<KnowledgeFactDto>)handlerResult);

        var result = await facade.GetRelatedFactsAsync("Checkout Service", ScopeId.New());

        var returned = Assert.Single(result);
        Assert.Equal(factId, returned.FactId);
        Assert.Equal(scopeId, returned.ScopeId);
        Assert.Equal("Checkout Service", returned.Subject);
        Assert.Equal("Service", returned.SubjectType);
        Assert.Equal("DEPENDS_ON", returned.Predicate);
        Assert.Equal("Payments DB", returned.Object);
        Assert.Equal("Service", returned.ObjectType);
        Assert.Equal(3, returned.Distance);
        Assert.Equal(timestamp, returned.Timestamp);
    }

    [Fact]
    public async Task GetRelatedFactsAsync_ForwardsEntityScopeAndDepth()
    {
        var (facade, mediator) = BuildFacade();
        GetRelatedFactsQuery? dispatched = null;
        var scopeId = ScopeId.New();

        mediator
            .Setup(m => m.Send(It.IsAny<GetRelatedFactsQuery>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<IReadOnlyList<KnowledgeFactDto>>, CancellationToken>(
                (q, _) => dispatched = (GetRelatedFactsQuery)q)
            .ReturnsAsync((IReadOnlyList<KnowledgeFactDto>)new List<KnowledgeFactDto>());

        await facade.GetRelatedFactsAsync("Checkout Service", scopeId, maxDepth: 4);

        Assert.NotNull(dispatched);
        Assert.Equal("Checkout Service", dispatched!.EntityName);
        Assert.Equal(scopeId, dispatched.ScopeId);
        Assert.Equal(4, dispatched.MaxDepth);
    }

    [Fact]
    public async Task GetRelatedFactsAsync_ForwardsTheCallersCancellationToken()
    {
        var (facade, mediator) = BuildFacade();

        mediator
            .Setup(m => m.Send(It.IsAny<GetRelatedFactsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<KnowledgeFactDto>)new List<KnowledgeFactDto>());

        using var cts = new CancellationTokenSource();

        await facade.GetRelatedFactsAsync("Checkout Service", ScopeId.New(), cancellationToken: cts.Token);

        mediator.Verify(
            m => m.Send(It.IsAny<GetRelatedFactsQuery>(), cts.Token),
            Times.Once);
    }

    #endregion

    #region AddKnowledgeFactAsync

    [Fact]
    public async Task AddKnowledgeFactAsync_DefaultsSubjectAndObjectTypeToEntity()
    {
        var (facade, mediator) = BuildFacade();
        CreateKnowledgeFactCommand? dispatched = null;

        mediator
            .Setup(m => m.Send(It.IsAny<CreateKnowledgeFactCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<KnowledgeFactDto?>, CancellationToken>((c, _) => dispatched = (CreateKnowledgeFactCommand)c)
            .ReturnsAsync((KnowledgeFactDto?)new KnowledgeFactDto { Subject = "Checkout Service", Predicate = "DEPENDS_ON", Object = "Payments DB" });

        await facade.AddKnowledgeFactAsync("Checkout Service", "DEPENDS_ON", "Payments DB", ScopeId.New());

        Assert.NotNull(dispatched);
        Assert.Equal("Entity", dispatched!.SubjectType);
        Assert.Equal("Entity", dispatched.ObjectType);
    }

    [Fact]
    public async Task AddKnowledgeFactAsync_HandlerReturnsFact_ReturnsTrue()
    {
        var (facade, mediator) = BuildFacade();

        mediator
            .Setup(m => m.Send(It.IsAny<CreateKnowledgeFactCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((KnowledgeFactDto?)new KnowledgeFactDto { Subject = "Checkout Service", Predicate = "DEPENDS_ON", Object = "Payments DB" });

        var written = await facade.AddKnowledgeFactAsync("Checkout Service", "DEPENDS_ON", "Payments DB", ScopeId.New());

        Assert.True(written);
    }

    [Fact]
    public async Task AddKnowledgeFactAsync_HandlerReturnsNull_ReturnsFalse()
    {
        // The handler returns null when the predicate is not one of the allowed relationship
        // types; the facade must surface that as "nothing was written" rather than throwing or
        // fabricating a result.
        var (facade, mediator) = BuildFacade();

        mediator
            .Setup(m => m.Send(It.IsAny<CreateKnowledgeFactCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((KnowledgeFactDto?)null);

        var written = await facade.AddKnowledgeFactAsync("Checkout Service", "UNKNOWN_PREDICATE", "Payments DB", ScopeId.New());

        Assert.False(written);
    }

    [Fact]
    public async Task AddKnowledgeFactAsync_ForwardsScopeSubjectPredicateAndObject()
    {
        var (facade, mediator) = BuildFacade();
        CreateKnowledgeFactCommand? dispatched = null;
        var scopeId = ScopeId.New();

        mediator
            .Setup(m => m.Send(It.IsAny<CreateKnowledgeFactCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<KnowledgeFactDto?>, CancellationToken>((c, _) => dispatched = (CreateKnowledgeFactCommand)c)
            .ReturnsAsync((KnowledgeFactDto?)new KnowledgeFactDto { Subject = "Checkout Service", Predicate = "DEPENDS_ON", Object = "Payments DB" });

        await facade.AddKnowledgeFactAsync(
            "Checkout Service", "DEPENDS_ON", "Payments DB", scopeId,
            subjectType: "Service", objectType: "Service");

        Assert.NotNull(dispatched);
        Assert.Equal(scopeId.Value, dispatched!.ScopeId);
        Assert.Equal("Checkout Service", dispatched.SubjectName);
        Assert.Equal("DEPENDS_ON", dispatched.Predicate);
        Assert.Equal("Payments DB", dispatched.ObjectName);
        Assert.Equal("Service", dispatched.SubjectType);
        Assert.Equal("Service", dispatched.ObjectType);
    }

    [Fact]
    public async Task AddKnowledgeFactAsync_ForwardsTheCallersCancellationToken()
    {
        var (facade, mediator) = BuildFacade();

        mediator
            .Setup(m => m.Send(It.IsAny<CreateKnowledgeFactCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((KnowledgeFactDto?)new KnowledgeFactDto { Subject = "Checkout Service", Predicate = "DEPENDS_ON", Object = "Payments DB" });

        using var cts = new CancellationTokenSource();

        await facade.AddKnowledgeFactAsync("Checkout Service", "DEPENDS_ON", "Payments DB", ScopeId.New(), cancellationToken: cts.Token);

        mediator.Verify(m => m.Send(It.IsAny<CreateKnowledgeFactCommand>(), cts.Token), Times.Once);
    }

    #endregion
}
