using Dragonmind.Core.Application.AntiCorruptionLayer;
using Dragonmind.Core.Application.AntiCorruptionLayer.DTOs;
using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Infrastructure.AntiCorruptionLayer;
using Dragonmind.Knowledge.Infrastructure.Caching;

using Microsoft.Extensions.Logging;

using Moq;

namespace Dragonmind.Knowledge.UnitTests.Infrastructure;

/// <summary>
/// Unit tests for <see cref="CachingKnowledgeContextFacade"/>, the cache-aside decorator over
/// <see cref="IKnowledgeContextFacade"/>.
/// </summary>
public class CachingKnowledgeContextFacadeTests
{
    private readonly Mock<IKnowledgeContextFacade> _mockInner;
    private readonly Mock<IKnowledgeCacheService> _mockCacheService;
    private readonly Mock<ILogger<CachingKnowledgeContextFacade>> _mockLogger;
    private readonly CachingKnowledgeContextFacade _sut;

    public CachingKnowledgeContextFacadeTests()
    {
        _mockInner = new Mock<IKnowledgeContextFacade>();
        _mockCacheService = new Mock<IKnowledgeCacheService>();
        _mockLogger = new Mock<ILogger<CachingKnowledgeContextFacade>>();
        _sut = new CachingKnowledgeContextFacade(
            _mockInner.Object,
            _mockCacheService.Object,
            _mockLogger.Object);
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_NullInner_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new CachingKnowledgeContextFacade(null!, _mockCacheService.Object, _mockLogger.Object));
    }

    [Fact]
    public void Constructor_NullCacheService_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new CachingKnowledgeContextFacade(_mockInner.Object, null!, _mockLogger.Object));
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new CachingKnowledgeContextFacade(_mockInner.Object, _mockCacheService.Object, null!));
    }

    #endregion

    #region SearchKnowledgeAsync Tests

    [Fact]
    public async Task SearchKnowledgeAsync_DelegatesToInner()
    {
        // Arrange
        var query = "checkout failures";
        var maxResults = 5;
        // A real id, matched exactly below. It.IsAny<ScopeId?> here would let a decorator that
        // always forwarded null pass — and dropping the scope is silent and global, so pure
        // delegation is exactly what has to be pinned.
        var scopeId = ScopeId.From(Guid.Parse("5eb25ffc-3fc3-ca8f-1fa8-a603d0422190"));
        var expectedResults = new List<KnowledgeSnippetDto>
        {
            new()
            {
                DocumentId = Guid.NewGuid(),
                Content = "The Checkout Service retries a failed payment.",
                RelevanceScore = 0.95,
                Source = "runbook"
            }
        };

        _mockInner
            .Setup(x => x.SearchKnowledgeAsync(query, scopeId, maxResults, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResults);

        // Act
        var result = await _sut.SearchKnowledgeAsync(query, scopeId, maxResults: maxResults);

        // Assert
        Assert.Equal(expectedResults, result);
        _mockInner.Verify(
            x => x.SearchKnowledgeAsync(query, scopeId, maxResults, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockInner.Verify(
            x => x.SearchKnowledgeAsync(It.IsAny<string>(), null, It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SearchKnowledgeAsync_ForwardsAnUnscopedSearchUnchanged()
    {
        // null is a legitimate mode — "search every scope" — and the decorator must not invent a
        // scope any more than it may drop one.
        _mockInner
            .Setup(x => x.SearchKnowledgeAsync(It.IsAny<string>(), It.IsAny<ScopeId?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<KnowledgeSnippetDto>());

        await _sut.SearchKnowledgeAsync("checkout failures", scopeId: null, maxResults: 5);

        _mockInner.Verify(
            x => x.SearchKnowledgeAsync("checkout failures", null, 5, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SearchKnowledgeAsync_NeverChecksCache()
    {
        // Arrange
        _mockInner
            .Setup(x => x.SearchKnowledgeAsync(It.IsAny<string>(), It.IsAny<ScopeId?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<KnowledgeSnippetDto>());

        // Act
        await _sut.SearchKnowledgeAsync("test query", scopeId: null);

        // Assert — semantic search must bypass cache entirely
        _mockCacheService.VerifyNoOtherCalls();
    }

    #endregion

    #region StoreKnowledgeAsync Tests

    [Fact]
    public async Task StoreKnowledgeAsync_DelegatesToInner()
    {
        // Arrange
        var content = "The Checkout Service retries a failed payment up to three times.";
        var source = "runbook";
        var scopeId = ScopeId.From(Guid.NewGuid());
        var expectedDocId = DocumentId.New();

        _mockInner
            .Setup(x => x.StoreKnowledgeAsync(content, source, scopeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedDocId);

        // Act
        var result = await _sut.StoreKnowledgeAsync(content, source, scopeId);

        // Assert
        Assert.Equal(expectedDocId, result);
        _mockInner.Verify(x => x.StoreKnowledgeAsync(content, source, scopeId, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region GetRelatedFactsAsync Tests

    [Fact]
    public async Task GetRelatedFactsAsync_DelegatesToCacheServices_GetOrLoad()
    {
        // Arrange
        var entityName = "Checkout Service";
        var scopeId = ScopeId.From(Guid.NewGuid());
        var maxDepth = 2;
        var cachedFacts = CreateTestFacts();

        _mockCacheService
            .Setup(x => x.GetOrLoadRelatedFactsAsync(
                scopeId, entityName, maxDepth,
                It.IsAny<Func<CancellationToken, Task<IReadOnlyList<KnowledgeFactDto>>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cachedFacts);

        // Act
        var result = await _sut.GetRelatedFactsAsync(entityName, scopeId, maxDepth);

        // Assert — the decorator hands the whole cache-aside decision to the cache service; it must
        // not perform a separate cache read/write of its own around it.
        Assert.Equal(cachedFacts, result);
        _mockInner.Verify(
            x => x.GetRelatedFactsAsync(It.IsAny<string>(), It.IsAny<ScopeId>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetRelatedFactsAsync_PassesALoadCallbackThatDelegatesToInner()
    {
        // Arrange
        var entityName = "Checkout Service";
        var scopeId = ScopeId.From(Guid.NewGuid());
        var maxDepth = 2;
        var facts = CreateTestFacts();

        _mockInner
            .Setup(x => x.GetRelatedFactsAsync(entityName, scopeId, maxDepth, It.IsAny<CancellationToken>()))
            .ReturnsAsync(facts);

        Func<CancellationToken, Task<IReadOnlyList<KnowledgeFactDto>>>? capturedLoad = null;
        _mockCacheService
            .Setup(x => x.GetOrLoadRelatedFactsAsync(
                scopeId, entityName, maxDepth,
                It.IsAny<Func<CancellationToken, Task<IReadOnlyList<KnowledgeFactDto>>>>(),
                It.IsAny<CancellationToken>()))
            .Callback<ScopeId, string, int, Func<CancellationToken, Task<IReadOnlyList<KnowledgeFactDto>>>, CancellationToken>(
                (_, _, _, load, _) => capturedLoad = load)
            .Returns<ScopeId, string, int, Func<CancellationToken, Task<IReadOnlyList<KnowledgeFactDto>>>, CancellationToken>(
                (_, _, _, load, ct) => load(ct));

        // Act
        var result = await _sut.GetRelatedFactsAsync(entityName, scopeId, maxDepth);

        // Assert — the callback passed to the cache service is exactly a call to `_inner`, proving
        // the decorator never fetches the graph data itself.
        Assert.NotNull(capturedLoad);
        Assert.Equal(facts, result);
        _mockInner.Verify(x => x.GetRelatedFactsAsync(entityName, scopeId, maxDepth, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetRelatedFactsAsync_SameEntityDifferentScopes_QueriesCacheSeparately()
    {
        // Two scopes naming the same entity must never share a cache lookup — a decorator that
        // keyed only on entity name would let scope B's cached facts leak into scope A's read.
        var entityName = "Checkout Service";
        var scopeA = ScopeId.From(Guid.NewGuid());
        var scopeB = ScopeId.From(Guid.NewGuid());

        _mockCacheService
            .Setup(x => x.GetOrLoadRelatedFactsAsync(
                It.IsAny<ScopeId>(), entityName, It.IsAny<int>(),
                It.IsAny<Func<CancellationToken, Task<IReadOnlyList<KnowledgeFactDto>>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateTestFacts());

        await _sut.GetRelatedFactsAsync(entityName, scopeA);
        await _sut.GetRelatedFactsAsync(entityName, scopeB);

        _mockCacheService.Verify(
            x => x.GetOrLoadRelatedFactsAsync(
                scopeA, entityName, It.IsAny<int>(),
                It.IsAny<Func<CancellationToken, Task<IReadOnlyList<KnowledgeFactDto>>>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _mockCacheService.Verify(
            x => x.GetOrLoadRelatedFactsAsync(
                scopeB, entityName, It.IsAny<int>(),
                It.IsAny<Func<CancellationToken, Task<IReadOnlyList<KnowledgeFactDto>>>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region AddKnowledgeFactAsync Tests

    [Fact]
    public async Task AddKnowledgeFactAsync_DelegatesToInner()
    {
        // Arrange
        var subject = "Checkout Service";
        var predicate = "DEPENDS_ON";
        var @object = "Payments DB";
        var scopeId = ScopeId.From(Guid.NewGuid());

        _mockInner
            .Setup(x => x.AddKnowledgeFactAsync(subject, predicate, @object, scopeId, "Entity", "Entity", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        await _sut.AddKnowledgeFactAsync(subject, predicate, @object, scopeId);

        // Assert
        _mockInner.Verify(
            x => x.AddKnowledgeFactAsync(subject, predicate, @object, scopeId, "Entity", "Entity", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AddKnowledgeFactAsync_WriteSucceeds_ReturnsTrue()
    {
        var scopeId = ScopeId.From(Guid.NewGuid());

        _mockInner
            .Setup(x => x.AddKnowledgeFactAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<ScopeId>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var written = await _sut.AddKnowledgeFactAsync("Checkout Service", "DEPENDS_ON", "Payments DB", scopeId);

        Assert.True(written);
    }

    [Fact]
    public async Task AddKnowledgeFactAsync_WriteRejected_ReturnsFalse_AndDoesNotInvalidate()
    {
        // A rejected predicate writes nothing, so there is nothing new to invalidate for — and
        // invalidating anyway would just discard perfectly valid cached facts for no reason.
        var scopeId = ScopeId.From(Guid.NewGuid());

        _mockInner
            .Setup(x => x.AddKnowledgeFactAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<ScopeId>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var written = await _sut.AddKnowledgeFactAsync("Checkout Service", "UNKNOWN_PREDICATE", "Payments DB", scopeId);

        Assert.False(written);
        _mockCacheService.Verify(
            x => x.InvalidateScopeAsync(It.IsAny<ScopeId>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AddKnowledgeFactAsync_WriteSucceeds_InvalidatesScopeCache()
    {
        // Arrange
        var subject = "Checkout Service";
        var predicate = "DEPENDS_ON";
        var @object = "Payments DB";
        var scopeId = ScopeId.From(Guid.NewGuid());

        _mockCacheService
            .Setup(x => x.InvalidateScopeAsync(scopeId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockInner
            .Setup(x => x.AddKnowledgeFactAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<ScopeId>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        await _sut.AddKnowledgeFactAsync(subject, predicate, @object, scopeId);

        // Assert
        _mockCacheService.Verify(
            x => x.InvalidateScopeAsync(scopeId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AddKnowledgeFactAsync_InvalidatesAfterDelegating_WithNoneToken()
    {
        // The write must land first: invalidating before a write that then fails would rotate the
        // generation for nothing, and (per the caller-cancellation guarantee below) the
        // invalidation call itself must use CancellationToken.None rather than the caller's token.
        var subject = "Checkout Service";
        var predicate = "DEPENDS_ON";
        var @object = "Payments DB";
        var scopeId = ScopeId.From(Guid.NewGuid());
        var callOrder = new List<string>();

        _mockInner
            .Setup(x => x.AddKnowledgeFactAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<ScopeId>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("inner"))
            .ReturnsAsync(true);

        _mockCacheService
            .Setup(x => x.InvalidateScopeAsync(scopeId, CancellationToken.None))
            .Callback(() => callOrder.Add("invalidate"))
            .Returns(Task.CompletedTask);

        // Act — pass a live (non-default) token to prove the invalidation call still uses None.
        using var cts = new CancellationTokenSource();
        await _sut.AddKnowledgeFactAsync(subject, predicate, @object, scopeId, cancellationToken: cts.Token);

        // Assert — write happens before invalidation, and invalidation used CancellationToken.None
        Assert.Equal(new[] { "inner", "invalidate" }, callOrder);
        _mockCacheService.Verify(x => x.InvalidateScopeAsync(scopeId, CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task AddKnowledgeFactAsync_InnerThrows_StillInvalidatesCache()
    {
        // This test previously asserted the opposite, on the reasoning that a failed write has
        // nothing to invalidate for. That assumed a throw means the write did not happen, which is
        // not true here: the repository forwards the caller's token to an AUTOCOMMIT graph write, so
        // the fact can land and the cancellation only be observed as the await completes. Under the
        // old contract that path left the pre-write generation reachable with the fact in the graph.
        //
        // A throw means "do not know", and that has to count as "did". A REJECTED write is different
        // and still invalidates nothing -- it completed normally having written nothing.
        var subject = "Checkout Service";
        var predicate = "DEPENDS_ON";
        var @object = "Payments DB";
        var scopeId = ScopeId.From(Guid.NewGuid());

        _mockInner
            .Setup(x => x.AddKnowledgeFactAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<ScopeId>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("write failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.AddKnowledgeFactAsync(subject, predicate, @object, scopeId));

        _mockCacheService.Verify(
            x => x.InvalidateScopeAsync(scopeId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region Helper Methods

    private static IReadOnlyList<KnowledgeFactDto> CreateTestFacts()
    {
        return new List<KnowledgeFactDto>
        {
            new()
            {
                Subject = "Checkout Service",
                Predicate = "IS_A",
                Object = "Service",
                Distance = 1
            },
            new()
            {
                Subject = "Checkout Service",
                Predicate = "DEPENDS_ON",
                Object = "Payments DB",
                Distance = 1
            }
        };
    }

    #endregion

    [Fact]
    public async Task AddKnowledgeFactAsync_WhenTheWriteThrows_StillInvalidatesTheScope()
    {
        // The repository forwards the caller's token to an autocommit graph write, so a cancellation
        // can be observed AFTER the fact has already landed. `written` is false on that path even
        // though the fact may be in the graph, so "did not complete" has to count as "may have
        // written" -- otherwise the pre-write generation stays reachable.
        var scopeId = ScopeId.From(Guid.NewGuid());

        _mockInner
            .Setup(x => x.AddKnowledgeFactAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ScopeId>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _sut.AddKnowledgeFactAsync("Checkout Service", "DEPENDS_ON", "Payments DB", scopeId));

        _mockCacheService.Verify(
            x => x.InvalidateScopeAsync(scopeId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AddKnowledgeFactAsync_WhenTheWriteIsRejected_DoesNotInvalidateTheScope()
    {
        // A rejected predicate wrote nothing and completed normally, so there is nothing to
        // invalidate. This is the one case that must NOT rotate, and it is what keeps the
        // throw-path rotation from being indistinguishable from "always rotate".
        var scopeId = ScopeId.From(Guid.NewGuid());

        _mockInner
            .Setup(x => x.AddKnowledgeFactAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ScopeId>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var written = await _sut.AddKnowledgeFactAsync("Checkout Service", "NOT_A_REAL_PREDICATE", "Payments DB", scopeId);

        Assert.False(written);
        _mockCacheService.Verify(
            x => x.InvalidateScopeAsync(It.IsAny<ScopeId>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

}
