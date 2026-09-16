using Dragonmind.Core.Application.AntiCorruptionLayer;
using Dragonmind.Core.Application.AntiCorruptionLayer.DTOs;
using Dragonmind.Core.Application.Caching;
using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Infrastructure.AntiCorruptionLayer;
using Dragonmind.Knowledge.Infrastructure.Caching;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace Dragonmind.Knowledge.UnitTests.Infrastructure;

/// <summary>
/// Reproduces the generation race fixed alongside <see cref="KnowledgeCacheService.GetOrLoadRelatedFactsAsync"/>:
/// a concurrent <c>AddKnowledgeFactAsync</c> (and the scope-generation rotation it triggers) landing
/// WHILE a related-facts graph load is still in flight must not let the pre-write, now-stale facts get
/// cached and served back out afterwards. Exercised against the REAL <see cref="KnowledgeCacheService"/>
/// over an in-memory <see cref="ICacheService"/> fake, rather than a mocked cache service, because the
/// bug lived in how the generation was (or wasn't) shared between the read and the write around the
/// load — behavior a mock of the old two-method API could not have caught either side of the fix.
/// </summary>
public class CachingKnowledgeContextFacadeGenerationRaceTests
{
    [Fact]
    public async Task GetRelatedFactsAsync_WhenTheScopeIsInvalidatedDuringTheLoad_DoesNotServeThePreWriteFactsAfterwards()
    {
        // Arrange
        var fakeCache = new InMemoryCacheServiceFake();
        var cacheService = new KnowledgeCacheService(fakeCache, NullLogger<KnowledgeCacheService>.Instance);
        var mockInner = new Mock<IKnowledgeContextFacade>();
        var scopeId = ScopeId.From(Guid.NewGuid());
        const string entityName = "Checkout Service";

        var staleFacts = new List<KnowledgeFactDto>
        {
            new() { Subject = "Checkout Service", Predicate = "DEPENDS_ON", Object = "StalePaymentsDb", Distance = 1 }
        };
        var freshFacts = new List<KnowledgeFactDto>
        {
            new() { Subject = "Checkout Service", Predicate = "DEPENDS_ON", Object = "FreshPaymentsDb", Distance = 1 }
        };

        CachingKnowledgeContextFacade? sut = null;
        var loadCallCount = 0;

        // The concurrent write must actually persist for it to rotate the generation — the decorator
        // only invalidates when the inner write reports success.
        mockInner
            .Setup(x => x.AddKnowledgeFactAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<ScopeId>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        mockInner
            .Setup(x => x.GetRelatedFactsAsync(entityName, scopeId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                loadCallCount++;
                if (loadCallCount == 1)
                {
                    // Mid-flight, a different code path writing several facts concurrently for the
                    // SAME scope adds a fact, which rotates its cache generation via
                    // InvalidateScopeAsync before this load returns its (now stale) result.
                    await sut!.AddKnowledgeFactAsync("Checkout Service", "DEPENDS_ON", "PaymentsDb", scopeId);
                    return (IReadOnlyList<KnowledgeFactDto>)staleFacts;
                }

                return (IReadOnlyList<KnowledgeFactDto>)freshFacts;
            });

        sut = new CachingKnowledgeContextFacade(
            mockInner.Object,
            cacheService,
            NullLogger<CachingKnowledgeContextFacade>.Instance);

        // The scope must already have a generation marker, or the first read takes the
        // "no resolvable generation" path, bypasses the cache entirely and never writes anything --
        // at which point there is no poisoned entry for this test to be about, and it would pass with
        // the generation-pinning fix reverted. Rotate once up front so the read takes the caching path.
        await cacheService.InvalidateScopeAsync(scopeId);

        // Act
        var firstRead = await sut.GetRelatedFactsAsync(entityName, scopeId);
        var secondRead = await sut.GetRelatedFactsAsync(entityName, scopeId);

        // Assert — the first read legitimately returns the stale facts (that IS what the in-flight
        // load returned), but the write that raced it must not have poisoned the cache: the second
        // read must go back to the inner facade and return the fresh facts, not a cached stale entry.
        Assert.Equal("StalePaymentsDb", Assert.Single(firstRead).Object);
        Assert.Equal("FreshPaymentsDb", Assert.Single(secondRead).Object);
        Assert.Equal(2, loadCallCount);
    }

    /// <summary>
    /// Minimal dictionary-backed <see cref="ICacheService"/> double for exercising the real
    /// <see cref="KnowledgeCacheService"/> without Redis. Not thread-safe — sufficient for these
    /// sequential-await tests, which model the race through ordering rather than actual concurrency.
    /// </summary>
    private sealed class InMemoryCacheServiceFake : ICacheService
    {
        private readonly Dictionary<string, object> _store = new();

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
        {
            return Task.FromResult(_store.TryGetValue(key, out var value) ? (T?)value : null);
        }

        public Task SetAsync<T>(string key, T value, CacheEntryOptions? options = null, CancellationToken cancellationToken = default) where T : class
        {
            _store[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            _store.Remove(key);
            return Task.CompletedTask;
        }

        public async Task<T> GetOrSetAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CacheEntryOptions? options = null, CancellationToken cancellationToken = default) where T : class
        {
            if (_store.TryGetValue(key, out var existing))
            {
                return (T)existing;
            }

            var created = await factory(cancellationToken);
            _store[key] = created;
            return created;
        }

        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(_store.ContainsKey(key));
    }
}
