using System.ComponentModel.DataAnnotations;

using Dragonmind.Core.AI;
using Dragonmind.Core.Application.AntiCorruptionLayer;
using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Domain.Repositories;
using Dragonmind.Knowledge.Domain.ValueObjects;
using Dragonmind.Knowledge.Infrastructure.AntiCorruptionLayer;
using Dragonmind.Knowledge.Infrastructure.DI;
using Dragonmind.Knowledge.Infrastructure.Persistence.Repositories;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Npgsql;

using IDragonmindEmbeddingGenerator = Dragonmind.Core.AI.IEmbeddingGenerator;

namespace Dragonmind.Knowledge.IntegrationTests;

/// <summary>
/// End-to-end integration tests for the only path a consumer is meant to use: <c>AddKnowledgeContext</c>
/// DI, resolving <see cref="IKnowledgeContextFacade"/> (as <see cref="CachingKnowledgeContextFacade"/>
/// wrapping <see cref="KnowledgeContextFacade"/>), dispatching through MediatR
/// (<c>ValidationPipelineBehavior</c> and <c>LoggingPipelineBehavior</c>) to command/query handlers,
/// through domain services, to the real pgvector and Apache AGE repositories, and back out as DTOs.
/// <para>
/// None of the other integration test classes in this project go through the facade: most construct a
/// repository directly, and <c>MigrationTests</c> drives the <c>DbContext</c> with raw SQL. So none of
/// them exercise DI wiring, the MediatR pipeline behaviors, or the caching decorator against a live
/// backend, and the shipped <see cref="ExtensionsAIEmbeddingGenerator"/> adapter had only run in unit
/// tests against a mocked provider, never inside the <c>AddKnowledgeContext</c> graph. This class
/// closes that gap: every Act step
/// below goes through <see cref="IKnowledgeContextFacade"/>. Raw repository instances are used only
/// for "nothing was persisted" assertions and for teardown.
/// </para>
/// </summary>
[Collection("Postgres")]
public sealed class KnowledgeContextFacadeEndToEndIntegrationTests : IAsyncLifetime
{
    /// <summary>
    /// The spelling a predicate comes back in through the facade. Facts are written in the canonical
    /// <see cref="RelationshipTypes"/> form (<c>DEPENDS_ON</c>), and <c>AgeFactRowMapper.TryMap</c>
    /// deliberately restores spaces on the way out, because a fact is rendered verbatim into a
    /// downstream prompt. Asserting the exact read spelling here pins that contract end-to-end: it is
    /// what a consumer of <see cref="IKnowledgeContextFacade.GetRelatedFactsAsync"/> actually receives.
    /// </summary>
    private const string DependsOnAsRead = "DEPENDS ON";

    /// <inheritdoc cref="DependsOnAsRead"/>
    private const string PartOfAsRead = "PART OF";

    private readonly PostgresFixture _fixture;
    private readonly List<ScopeId> _trackedScopeIds = new();
    private readonly List<string> _trackedEntityNames = new();

    private ServiceProvider _serviceProvider = null!;
    private CountingDistributedCache _cache = null!;
    private IKnowledgeGraphRepository _rawGraphRepository = null!;
    private IKnowledgeDocumentRepository _rawDocumentRepository = null!;

    public KnowledgeContextFacadeEndToEndIntegrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Builds this test class's own DI container over the fixture's already-migrated database — it
    /// does not re-run migrations, the collection fixture already did that once. This mirrors exactly
    /// what a host application wires: logging, a distributed cache, an
    /// <see cref="IDragonmindEmbeddingGenerator"/>, and <c>AddKnowledgeContext</c> itself.
    /// <para>
    /// xUnit v3 does not call <see cref="DisposeAsync"/> when this method throws, so nothing acquired
    /// here before a throw would ever be cleaned up. Today the only step that can throw is
    /// <see cref="BuildServiceProvider"/> (<c>ValidateOnBuild</c> rejecting the graph), and nothing
    /// before it holds a resource; keep it that way, or clean up here before rethrowing.
    /// </para>
    /// </summary>
    public ValueTask InitializeAsync()
    {
        _cache = new CountingDistributedCache(
            new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        _serviceProvider = BuildServiceProvider(_fixture.DataSource, new HashingEmbeddingGenerator(), _cache);

        // Raw repositories over the FIXTURE's context factory (not this test's own container) — used
        // only for "nothing was persisted" assertions and teardown. Every Act step goes through the
        // facade resolved from _serviceProvider instead.
        _rawGraphRepository = new ApacheAgeKnowledgeGraphRepository(
            _fixture.ContextFactory, NullLogger<ApacheAgeKnowledgeGraphRepository>.Instance);
        _rawDocumentRepository = new EfCoreKnowledgeDocumentRepository(_fixture.ContextFactory);

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Hands off to the same tracked-cleanup helpers the rest of this project uses. No fact ids are
    /// passed: the facade never returns the id it assigned, and it does not need to, because every
    /// fact this class writes has both endpoints minted by <see cref="CreateUniqueEntityName"/>, so the
    /// name-based <c>DETACH DELETE</c> in <see cref="TrackedFactCleanup"/> removes each edge along
    /// with its vertices.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Every step runs even if an earlier one throws, and every failure is reported. Graph cleanup
        // throws on purpose when a tracked vertex survives, and nested try/finally would let a later
        // step's exception silently replace that one. Skipping a step is not an option either: leaving
        // documents behind or the provider undisposed leaks into every later test class sharing the
        // database. Cancellation is not a cleanup failure, so it still propagates.
        var failures = new List<Exception>();

        await RunTeardownStepAsync(() => TrackedFactCleanup.DeleteTrackedFactsAsync(
            _rawGraphRepository, _fixture.ContextFactory, Array.Empty<FactId>(), _trackedEntityNames));
        await RunTeardownStepAsync(() => TrackedDocumentCleanup.DeleteTrackedDocumentsAsync(
            _fixture.ContextFactory, _trackedScopeIds));

        await RunTeardownStepAsync(() => _serviceProvider.DisposeAsync().AsTask());

        if (failures.Count > 0)
        {
            throw new AggregateException("Test teardown did not complete cleanly.", failures);
        }

        async Task RunTeardownStepAsync(Func<Task> step)
        {
            try
            {
                await step();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(ex);
            }
        }
    }

    /// <summary>
    /// Wires the Knowledge context exactly the way a host application would: a distributed cache and
    /// an <see cref="IDragonmindEmbeddingGenerator"/> registered before <c>AddKnowledgeContext</c>,
    /// which are the two dependencies <c>AddKnowledgeContext</c>'s own XML docs say the host must
    /// supply.
    /// </summary>
    private static ServiceProvider BuildServiceProvider(
        NpgsqlDataSource dataSource,
        IEmbeddingGenerator<string, Embedding<float>> innerEmbeddingGenerator,
        IDistributedCache distributedCache)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDistributedCache>(distributedCache);
        services.AddSingleton<IDragonmindEmbeddingGenerator>(provider =>
            new ExtensionsAIEmbeddingGenerator(
                innerEmbeddingGenerator,
                EmbeddingDimensions.Default,
                provider.GetRequiredService<ILogger<ExtensionsAIEmbeddingGenerator>>()));
        services.AddKnowledgeContext(dataSource);

        // The validation a host gets by default in Development: a captive dependency (a singleton
        // holding a scoped service) or an unresolvable registration fails the build here, instead of
        // resolving silently the way a default-options provider would let it.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
    }

    /// <summary>
    /// Resolves <see cref="IKnowledgeContextFacade"/> from a fresh DI scope for a single call, the
    /// way a host request pipeline would — never captured across calls, so no test can accidentally
    /// depend on scoped-service state surviving between two facade calls.
    /// </summary>
    private Task<T> UseFacadeAsync<T>(Func<IKnowledgeContextFacade, Task<T>> action)
        => UseFacadeAsync(_serviceProvider, action);

    private static async Task<T> UseFacadeAsync<T>(
        ServiceProvider serviceProvider, Func<IKnowledgeContextFacade, Task<T>> action)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var facade = scope.ServiceProvider.GetRequiredService<IKnowledgeContextFacade>();
        return await action(facade);
    }

    private ScopeId CreateTrackedScopeId()
    {
        var scopeId = ScopeId.New();
        _trackedScopeIds.Add(scopeId);
        return scopeId;
    }

    /// <summary>
    /// Builds an entity name unique to this test run (letters/digits only, the same shape
    /// <c>AgeKnowledgeGraphRepositoryIntegrationTests.CreateUniqueEntityName</c> uses) and tracks it
    /// for teardown.
    /// </summary>
    private string CreateUniqueEntityName(string baseName)
    {
        var name = baseName + Guid.NewGuid().ToString("N");
        _trackedEntityNames.Add(name);
        return name;
    }

    [Fact]
    public async Task AddKnowledgeContext_ResolvesFacade_AsCachingDecorator()
    {
        // Act
        await using var scope = _serviceProvider.CreateAsyncScope();
        var facade = scope.ServiceProvider.GetRequiredService<IKnowledgeContextFacade>();

        // Assert — the decorator, never the concrete facade, is the only thing a consumer may see.
        Assert.IsType<CachingKnowledgeContextFacade>(facade);
    }

    [Fact]
    public async Task SearchKnowledgeAsync_AfterStoreKnowledgeAsync_RanksMatchingDocumentFirstWithDescendingScores()
    {
        // Arrange — three documents with disjoint vocabularies, so their hashed embeddings barely overlap.
        var scopeId = CreateTrackedScopeId();
        var source = "checkout-runbook";
        var matchingContent = "The Checkout Service retries failed payment authorizations three times before paging on-call.";
        var otherContentA = "The nightly inventory sync batch job archives stale warehouse records after ninety days.";
        var otherContentB = "New engineer onboarding covers VPN setup and requesting access to the deployment pipeline.";

        var matchingDocumentId = await UseFacadeAsync(f => f.StoreKnowledgeAsync(matchingContent, source, scopeId));
        await UseFacadeAsync(f => f.StoreKnowledgeAsync(otherContentA, "inventory-runbook", scopeId));
        await UseFacadeAsync(f => f.StoreKnowledgeAsync(otherContentB, "onboarding-guide", scopeId));

        // Act — scoped search (the exact MATERIALIZED CTE path, not HNSW) using the matching
        // document's own content as the query, so its vector is identical to itself.
        var results = await UseFacadeAsync(f => f.SearchKnowledgeAsync(matchingContent, scopeId, maxResults: 10));

        // Assert — all three stored documents come back: the scoped path ranks every row in the scope
        // and similarity (1 - distance/2) is never negative, so minSimilarity 0 excludes none of them.
        // Pinning the count is what keeps the ordering loop below from passing vacuously on one row.
        Assert.Equal(3, results.Count);
        var top = results[0];
        Assert.Equal(matchingDocumentId.Value, top.DocumentId);
        Assert.Equal(matchingContent, top.Content);
        Assert.Equal(source, top.Source);

        for (var i = 1; i < results.Count; i++)
        {
            Assert.True(
                results[i - 1].RelevanceScore >= results[i].RelevanceScore,
                $"Results not ordered: [{i - 1}]={results[i - 1].RelevanceScore} < [{i}]={results[i].RelevanceScore}");
        }
    }

    [Fact]
    public async Task SearchKnowledgeAsync_ScopedSearch_NeverReturnsAnotherScopesDocument()
    {
        // Arrange — two scopes each store a document about the same topic, tied together only by a
        // per-test GUID token so an unrelated row from another test can never outrank either one.
        var scopeA = CreateTrackedScopeId();
        var scopeB = CreateTrackedScopeId();
        var token = Guid.NewGuid().ToString("N");
        var contentA = $"Scope A {token}: the deployment pipeline runs a canary before full rollout.";
        var contentB = $"Scope B {token}: the deployment pipeline runs a canary before full rollout.";

        var documentA = await UseFacadeAsync(f => f.StoreKnowledgeAsync(contentA, "deploy-runbook-a", scopeA));
        var documentB = await UseFacadeAsync(f => f.StoreKnowledgeAsync(contentB, "deploy-runbook-b", scopeB));

        // Act
        var resultsForA = await UseFacadeAsync(f => f.SearchKnowledgeAsync(token, scopeA, maxResults: 10));
        var resultsForB = await UseFacadeAsync(f => f.SearchKnowledgeAsync(token, scopeB, maxResults: 10));

        // Assert — scoped search, the exact path: each scope sees only its own document.
        Assert.Contains(resultsForA, r => r.DocumentId == documentA.Value);
        Assert.DoesNotContain(resultsForA, r => r.DocumentId == documentB.Value);
        Assert.Contains(resultsForB, r => r.DocumentId == documentB.Value);
        Assert.DoesNotContain(resultsForB, r => r.DocumentId == documentA.Value);

        // Assert — unscoped search: presence-only. The documents table is shared across every scope,
        // so this documents that an omitted scope searches everything; it is not a claim that an
        // unscoped search excludes anything. Unlike the scoped reads above, this path is HNSW-eligible
        // and therefore approximate; both rows carry the per-test GUID token that nothing else in the
        // table shares, which keeps them the nearest neighbours by a wide margin.
        var unscoped = await UseFacadeAsync(f => f.SearchKnowledgeAsync(token, scopeId: null, maxResults: 20));
        Assert.Contains(unscoped, r => r.DocumentId == documentA.Value);
        Assert.Contains(unscoped, r => r.DocumentId == documentB.Value);
    }

    [Fact]
    public async Task AddKnowledgeFactAsync_SpacedLowercasePredicates_NormalizeIntoChainAtDepthTwo()
    {
        // Arrange
        var scopeId = CreateTrackedScopeId();
        var checkoutService = CreateUniqueEntityName("CheckoutService");
        var paymentsDb = CreateUniqueEntityName("PaymentsDb");
        var primaryCluster = CreateUniqueEntityName("PrimaryCluster");

        // Act — a lowercase spaced predicate and a padded mixed-case one, chained two hops deep.
        var wroteFirst = await UseFacadeAsync(f => f.AddKnowledgeFactAsync(checkoutService, "depends on", paymentsDb, scopeId));
        var wroteSecond = await UseFacadeAsync(f => f.AddKnowledgeFactAsync(paymentsDb, "  Part Of  ", primaryCluster, scopeId));

        // Guard: both writes must have been accepted, or the traversal below would assert nothing.
        Assert.True(wroteFirst);
        Assert.True(wroteSecond);

        var related = await UseFacadeAsync(f => f.GetRelatedFactsAsync(checkoutService, scopeId, maxDepth: 2));

        // Assert — both predicates were normalized to the canonical allowlist form on the way in and
        // come back in the read spelling, each at the distance its position in the chain implies. The
        // count is pinned so an extra, unrelated fact reaching this traversal fails the test.
        Assert.Equal(2, related.Count);

        var dependsOn = related.Single(r => r.Predicate == DependsOnAsRead);
        Assert.Equal(1, dependsOn.Distance);
        Assert.Equal(checkoutService, dependsOn.Subject);
        Assert.Equal(paymentsDb, dependsOn.Object);

        var partOf = related.Single(r => r.Predicate == PartOfAsRead);
        Assert.Equal(2, partOf.Distance);
        Assert.Equal(paymentsDb, partOf.Subject);
        Assert.Equal(primaryCluster, partOf.Object);
    }

    [Fact]
    public async Task AddKnowledgeFactAsync_DisallowedPredicate_ReturnsFalseAndWritesNothing()
    {
        // Arrange
        var scopeId = CreateTrackedScopeId();
        var subject = CreateUniqueEntityName("Frontend");
        var @object = CreateUniqueEntityName("Backend");

        // Act — "HALLUCINATED" is not in RelationshipTypes.Allowed.
        var written = await UseFacadeAsync(f => f.AddKnowledgeFactAsync(subject, "HALLUCINATED", @object, scopeId));

        // Assert
        Assert.False(written);
        var scoped = await _rawGraphRepository.GetByScopeIdAsync(scopeId);
        Assert.Empty(scoped);
    }

    [Fact]
    public async Task GetRelatedFactsAsync_SharedEntityName_NeverWalksAnotherScopesEdge()
    {
        // Arrange — scope A holds X-DEPENDS_ON->Y, scope B holds Y-DEPENDS_ON->Z. Entity Y is the
        // literal same vertex in both facts (AGE matches vertices by name), so a traversal from X in
        // scope A that is not scope-constrained on every edge could walk straight through to Z.
        var scopeA = CreateTrackedScopeId();
        var scopeB = CreateTrackedScopeId();
        var x = CreateUniqueEntityName("Frontend");
        var y = CreateUniqueEntityName("Gateway");
        var z = CreateUniqueEntityName("Backend");

        // Guard: both writes must land, or "no leak" would be trivially true.
        Assert.True(await UseFacadeAsync(f => f.AddKnowledgeFactAsync(x, "DEPENDS_ON", y, scopeA)));
        Assert.True(await UseFacadeAsync(f => f.AddKnowledgeFactAsync(y, "DEPENDS_ON", z, scopeB)));

        // Act — read from both sides of the shared vertex, each in its own scope. This pins the
        // traversal, not the cache key: each scope has its own random generation token, so the two
        // reads could never share a cache entry even if the key lost its scope segment. That property
        // is pinned directly by KnowledgeCacheServiceTests.GetOrLoadRelatedFactsAsync_KeyContainsScopeAndGeneration.
        var relatedInA = await UseFacadeAsync(f => f.GetRelatedFactsAsync(x, scopeA, maxDepth: 2));
        var relatedInB = await UseFacadeAsync(f => f.GetRelatedFactsAsync(y, scopeB, maxDepth: 2));

        // Assert — scope A sees exactly its own edge, nothing touching Z.
        var onlyInA = Assert.Single(relatedInA);
        Assert.Equal(x, onlyInA.Subject);
        Assert.Equal(y, onlyInA.Object);
        Assert.Equal(DependsOnAsRead, onlyInA.Predicate);
        Assert.DoesNotContain(relatedInA, r => r.Subject == z || r.Object == z);

        // Assert — and the mirror: scope B sees only its own edge, nothing touching X.
        var onlyInB = Assert.Single(relatedInB);
        Assert.Equal(y, onlyInB.Subject);
        Assert.Equal(z, onlyInB.Object);
        Assert.DoesNotContain(relatedInB, r => r.Subject == x || r.Object == x);
    }

    [Fact]
    public async Task GetRelatedFactsAsync_WriteAfterCachedRead_InvalidatesTheCachedListAndServesBothFacts()
    {
        // Arrange — one scope, one subject, two objects written at different points in the sequence.
        // The second write is what matters: it lands AFTER the first fact list is already cached, so
        // only real invalidation can make the next read see it. A test that wrote everything up front
        // would pass even if invalidation were broken, because nothing was cached yet.
        var scopeId = CreateTrackedScopeId();
        var subject = CreateUniqueEntityName("Orders");
        var firstObject = CreateUniqueEntityName("Payments");
        var secondObject = CreateUniqueEntityName("Ledger");

        // Guard: the first write also mints this scope's generation token, so reads below are cacheable.
        Assert.True(await UseFacadeAsync(f => f.AddKnowledgeFactAsync(subject, "DEPENDS_ON", firstObject, scopeId)));

        // Act — read 1 populates the cache: the generation token is read (hit) and the fact list is not
        // there yet (miss).
        var hitBefore1 = _cache.HitCount;
        var missBefore1 = _cache.MissCount;
        var read1 = await UseFacadeAsync(f => f.GetRelatedFactsAsync(subject, scopeId, maxDepth: 2));

        Assert.Contains(read1, r => r.Object == firstObject && r.Predicate == DependsOnAsRead);
        Assert.Equal(missBefore1 + 1, _cache.MissCount);
        Assert.Equal(hitBefore1 + 1, _cache.HitCount);

        // Act — read 2 is identical, so both the generation token and the fact list come from cache.
        var hitBefore2 = _cache.HitCount;
        var missBefore2 = _cache.MissCount;
        var read2 = await UseFacadeAsync(f => f.GetRelatedFactsAsync(subject, scopeId, maxDepth: 2));

        Assert.Contains(read2, r => r.Object == firstObject && r.Predicate == DependsOnAsRead);
        Assert.Equal(missBefore2, _cache.MissCount);
        Assert.Equal(hitBefore2 + 2, _cache.HitCount);

        // Act — write a second fact into the same scope, invalidating the list cached above.
        Assert.True(await UseFacadeAsync(f => f.AddKnowledgeFactAsync(subject, "DEPENDS_ON", secondObject, scopeId)));

        var missBefore3 = _cache.MissCount;
        var read3 = await UseFacadeAsync(f => f.GetRelatedFactsAsync(subject, scopeId, maxDepth: 2));

        // Assert — the new fact is visible alongside the old one, and the read was a miss because the
        // rotated generation makes the previously cached key unreachable. This is what fails if the
        // cache key drops its generation segment, or if invalidation reuses the same token: read 3
        // would still be a hit, and would still return only the first fact.
        Assert.Contains(read3, r => r.Object == firstObject && r.Predicate == DependsOnAsRead);
        Assert.Contains(read3, r => r.Object == secondObject && r.Predicate == DependsOnAsRead);
        Assert.Equal(missBefore3 + 1, _cache.MissCount);

        // Act & Assert — the post-invalidation list is cached in turn, so the next identical read hits.
        var hitBefore4 = _cache.HitCount;
        var missBefore4 = _cache.MissCount;
        var read4 = await UseFacadeAsync(f => f.GetRelatedFactsAsync(subject, scopeId, maxDepth: 2));

        Assert.Equal(read3.Count, read4.Count);
        Assert.Equal(missBefore4, _cache.MissCount);
        Assert.Equal(hitBefore4 + 2, _cache.HitCount);
    }

    [Fact]
    public async Task StoreKnowledgeAsync_WhitespaceContent_ThrowsValidationExceptionFromPipeline()
    {
        // Arrange — the point is WHERE this is rejected: the real ValidationPipelineBehavior in the
        // MediatR pipeline turns the command's DataAnnotations into a ValidationException before the
        // handler runs at all. Every unit test mocks IMediator, so none of them can observe it; without
        // the behavior the request would reach the handler and fail later, as a different exception.
        var scopeId = CreateTrackedScopeId();

        // Act & Assert
        await Assert.ThrowsAsync<ValidationException>(
            () => UseFacadeAsync(f => f.StoreKnowledgeAsync("   ", "source", scopeId)));

        var scoped = await _rawDocumentRepository.GetByScopeIdAsync(scopeId);
        Assert.Empty(scoped);
    }

    [Fact]
    public async Task SearchKnowledgeAsync_EmptyQuery_ThrowsArgumentException()
    {
        // Arrange — an empty query sent through the facade built by the real DI graph is rejected with
        // ArgumentException. Several layers guard the same input with the same exception type, so this
        // cannot show which one fired; that the facade's own guard fires BEFORE dispatch is pinned by
        // KnowledgeContextFacadeTests.SearchKnowledgeAsync_BlankQuery_ThrowsBeforeDispatch.
        var scopeId = ScopeId.New();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => UseFacadeAsync(f => f.SearchKnowledgeAsync(string.Empty, scopeId)));
    }

    [Fact]
    public async Task StoreKnowledgeAsync_GeneratorReturnsNarrowVector_ThrowsAndPersistsNothing()
    {
        // Arrange — a provider returning 128 dimensions instead of the EmbeddingDimensions.Default the
        // pgvector column requires. This test builds its OWN container, because the embedding
        // generator is wired once, at container build time.
        var scopeId = CreateTrackedScopeId();

        await using var narrowServiceProvider = BuildServiceProvider(
            _fixture.DataSource, new HashingEmbeddingGenerator(dimensions: 128), _cache);

        // Act & Assert — the shipped ExtensionsAIEmbeddingGenerator refuses a shorter-than-expected
        // vector rather than let it reach the vector store.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => UseFacadeAsync(
                narrowServiceProvider,
                f => f.StoreKnowledgeAsync(
                    "This document must never reach the vector store.", "narrow-provider-test", scopeId)));

        var scoped = await _rawDocumentRepository.GetByScopeIdAsync(scopeId);
        Assert.Empty(scoped);
    }
}
