using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Application.Queries.GetRelatedFacts;
using Dragonmind.Knowledge.Domain.Aggregates.KnowledgeGraphAggregate;
using Dragonmind.Knowledge.Domain.DomainServices;
using Dragonmind.Knowledge.Domain.ValueObjects;

using Microsoft.Extensions.Logging;

using Moq;

namespace Dragonmind.Knowledge.UnitTests.Application;

/// <summary>
/// Covers <see cref="GetRelatedFactsQueryHandler"/>: it forwards the entity/scope/depth
/// unchanged, maps every field of a traversal result including <c>Distance</c>, turns a
/// traversal failure into an empty result with a warning naming the entity (rather than letting
/// the exception surface), and forwards the caller's cancellation token.
/// </summary>
public class GetRelatedFactsQueryHandlerTests
{
    private static (GetRelatedFactsQueryHandler Handler, Mock<IGraphTraversalService> TraversalService, Mock<ILogger<GetRelatedFactsQueryHandler>> Logger) Harness()
    {
        var traversalService = new Mock<IGraphTraversalService>();
        var logger = new Mock<ILogger<GetRelatedFactsQueryHandler>>();
        var handler = new GetRelatedFactsQueryHandler(traversalService.Object, logger.Object);
        return (handler, traversalService, logger);
    }

    private static KnowledgeFact BuildFact() => KnowledgeFact.CreateFromStrings(
        ScopeId.New(),
        "Checkout Service",
        "Service",
        "DEPENDS_ON",
        "Payments DB",
        "Service");

    [Fact]
    public async Task HandleAsync_NullQuery_ThrowsArgumentNullException()
    {
        var (handler, _, _) = Harness();

        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.HandleAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_ForwardsEntityScopeAndDepth()
    {
        var (handler, traversalService, _) = Harness();
        var scopeId = ScopeId.New();

        traversalService
            .Setup(s => s.GetRelatedFactsAsync(It.IsAny<string>(), It.IsAny<ScopeId>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(KnowledgeFact, int)>());

        var query = new GetRelatedFactsQuery("Checkout Service", scopeId, maxDepth: 4);

        await handler.HandleAsync(query, CancellationToken.None);

        traversalService.Verify(
            s => s.GetRelatedFactsAsync("Checkout Service", scopeId, 4, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_MapsEveryFieldIncludingDistance()
    {
        var (handler, traversalService, _) = Harness();
        var fact = BuildFact();

        traversalService
            .Setup(s => s.GetRelatedFactsAsync(It.IsAny<string>(), It.IsAny<ScopeId>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { (fact, 3) });

        var result = await handler.HandleAsync(new GetRelatedFactsQuery("Checkout Service", ScopeId.New()), CancellationToken.None);

        var dto = Assert.Single(result);
        Assert.Equal(fact.Id.Value, dto.FactId);
        Assert.Equal(fact.ScopeId.Value, dto.ScopeId);
        Assert.Equal(fact.Subject.Name, dto.Subject);
        Assert.Equal(fact.Subject.EntityType, dto.SubjectType);
        Assert.Equal(fact.Predicate, dto.Predicate);
        Assert.Equal(fact.Object.Name, dto.Object);
        Assert.Equal(fact.Object.EntityType, dto.ObjectType);
        Assert.Equal(fact.Timestamp, dto.Timestamp);

        // Distance is asserted on the DTO. This test is named for Distance but used to destructure
        // a (Fact, Distance) tuple and assert the tuple's element, so the handler could leave
        // dto.Distance at its default and still pass -- which is exactly what it did.
        Assert.Equal(3, dto.Distance);
    }

    [Fact]
    public async Task HandleAsync_WhenTraversalThrows_ReturnsEmptyAndLogsAWarningNamingTheEntity()
    {
        var (handler, traversalService, logger) = Harness();

        traversalService
            .Setup(s => s.GetRelatedFactsAsync(It.IsAny<string>(), It.IsAny<ScopeId>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("graph unavailable"));

        var result = await handler.HandleAsync(new GetRelatedFactsQuery("Checkout Service", ScopeId.New()), CancellationToken.None);

        Assert.Empty(result);

        logger.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("Checkout Service")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_ForwardsTheCallersCancellationToken()
    {
        var (handler, traversalService, _) = Harness();

        traversalService
            .Setup(s => s.GetRelatedFactsAsync(It.IsAny<string>(), It.IsAny<ScopeId>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(KnowledgeFact, int)>());

        using var cts = new CancellationTokenSource();

        await handler.HandleAsync(new GetRelatedFactsQuery("Checkout Service", ScopeId.New()), cts.Token);

        traversalService.Verify(
            s => s.GetRelatedFactsAsync(It.IsAny<string>(), It.IsAny<ScopeId>(), It.IsAny<int>(), cts.Token),
            Times.Once);
    }
}
