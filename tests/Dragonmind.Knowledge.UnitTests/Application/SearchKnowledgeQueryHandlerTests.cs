using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Application.DTOs;
using Dragonmind.Knowledge.Application.Queries.SearchKnowledge;
using Dragonmind.Knowledge.Domain.Aggregates.KnowledgeDocumentAggregate;
using Dragonmind.Knowledge.Domain.DomainServices;
using Dragonmind.Knowledge.Domain.ValueObjects;

using Moq;

namespace Dragonmind.Knowledge.UnitTests.Application;

/// <summary>
/// Covers <see cref="SearchKnowledgeQueryHandler"/>: it forwards the query's text/max/min
/// unchanged, converts an optional scope into the domain <see cref="ScopeId"/> the search service
/// expects (or passes <c>null</c> when none was supplied), maps every field of a result onto its
/// DTO, and forwards the caller's cancellation token.
/// </summary>
public class SearchKnowledgeQueryHandlerTests
{
    private static (SearchKnowledgeQueryHandler Handler, Mock<IVectorSearchService> SearchService) Harness()
    {
        var searchService = new Mock<IVectorSearchService>();
        var handler = new SearchKnowledgeQueryHandler(searchService.Object);
        return (handler, searchService);
    }

    private static KnowledgeDocument BuildDocument() => KnowledgeDocument.CreateWithEmbedding(
        ScopeId.New(),
        DocumentContent.Create("Checkout retries three times.", "runbook", "Service"),
        Embedding.Create(new float[] { 0.1f, 0.2f }));

    [Fact]
    public async Task HandleAsync_NullQuery_ThrowsArgumentNullException()
    {
        var (handler, _) = Harness();

        await Assert.ThrowsAsync<ArgumentNullException>(() => handler.HandleAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_ForwardsQueryTextMaxResultsAndMinSimilarity()
    {
        var (handler, searchService) = Harness();

        searchService
            .Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<ScopeId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(KnowledgeDocument, double)>());

        var query = new SearchKnowledgeQuery("checkout retries", maxResults: 12, minSimilarity: 0.4);

        await handler.HandleAsync(query, CancellationToken.None);

        searchService.Verify(
            s => s.SearchAsync("checkout retries", 12, 0.4, It.IsAny<ScopeId?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WithAScope_SearchesOnlyThatScope()
    {
        var (handler, searchService) = Harness();
        var scopeGuid = Guid.NewGuid();

        searchService
            .Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<ScopeId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(KnowledgeDocument, double)>());

        var query = new SearchKnowledgeQuery("checkout retries", scopeId: scopeGuid);

        await handler.HandleAsync(query, CancellationToken.None);

        searchService.Verify(
            s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<double>(), ScopeId.From(scopeGuid), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_WithoutAScope_PassesNullScope()
    {
        var (handler, searchService) = Harness();

        searchService
            .Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<ScopeId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(KnowledgeDocument, double)>());

        var query = new SearchKnowledgeQuery("checkout retries", scopeId: null);

        await handler.HandleAsync(query, CancellationToken.None);

        searchService.Verify(
            s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<double>(), null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_MapsSimilarityScoreAndEveryDocumentField()
    {
        var (handler, searchService) = Harness();
        var document = BuildDocument();

        searchService
            .Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<ScopeId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { (document, 0.93) });

        var result = await handler.HandleAsync(new SearchKnowledgeQuery("checkout retries"), CancellationToken.None);

        var mapped = Assert.Single(result);
        Assert.Equal(0.93, mapped.SimilarityScore);
        Assert.Equal(document.Id.Value, mapped.Document.DocumentId);
        Assert.Equal(document.ScopeId.Value, mapped.Document.ScopeId);
        Assert.Equal(document.Content.Value, mapped.Document.Content);
        Assert.Equal(document.Content.Source, mapped.Document.Source);
        Assert.Equal(document.Content.Category, mapped.Document.Category);
        Assert.Equal(document.Timestamp, mapped.Document.Timestamp);
        Assert.True(mapped.Document.HasEmbedding);
    }

    [Fact]
    public async Task HandleAsync_NoMatches_ReturnsAnEmptyList()
    {
        var (handler, searchService) = Harness();

        searchService
            .Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<ScopeId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(KnowledgeDocument, double)>());

        var result = await handler.HandleAsync(new SearchKnowledgeQuery("nothing matches"), CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task HandleAsync_ForwardsTheCallersCancellationToken()
    {
        var (handler, searchService) = Harness();

        searchService
            .Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<ScopeId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<(KnowledgeDocument, double)>());

        using var cts = new CancellationTokenSource();

        await handler.HandleAsync(new SearchKnowledgeQuery("checkout retries"), cts.Token);

        searchService.Verify(
            s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<ScopeId?>(), cts.Token),
            Times.Once);
    }
}
