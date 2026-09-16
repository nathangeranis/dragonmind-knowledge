using Dragonmind.Core.Application.AntiCorruptionLayer.DTOs;
using System.Security.Cryptography;
using System.Text;

using Dragonmind.Core.Application.Caching;
using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Knowledge.Infrastructure.Caching;

using Microsoft.Extensions.Logging;

using Moq;

namespace Dragonmind.Knowledge.UnitTests.Infrastructure;

/// <summary>
/// Unit tests for <see cref="KnowledgeCacheService"/>.
/// Tests cache key generation, scope/generation scoping, and delegation to <see cref="ICacheService"/>.
/// </summary>
public class KnowledgeCacheServiceTests
{
    private readonly Mock<ICacheService> _mockCacheService;
    private readonly Mock<ILogger<KnowledgeCacheService>> _mockLogger;
    private readonly KnowledgeCacheService _sut;

    public KnowledgeCacheServiceTests()
    {
        _mockCacheService = new Mock<ICacheService>();
        _mockLogger = new Mock<ILogger<KnowledgeCacheService>>();
        _sut = new KnowledgeCacheService(_mockCacheService.Object, _mockLogger.Object);
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_NullCacheService_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new KnowledgeCacheService(null!, _mockLogger.Object));
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new KnowledgeCacheService(_mockCacheService.Object, null!));
    }

    #endregion

    #region Helper Methods

    private static string GenerationKey(ScopeId scopeId)
        => $"knowledge:facts:v2:{scopeId.Value:N}:generation";

    /// <summary>
    /// Computes the entity segment of a related-facts cache key the way the service does: a lowercase
    /// SHA-256 hex of the EXACT name, so names differing only by case or spacing stay distinct.
    /// </summary>
    private static string EntitySegment(string entityName)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(entityName)));

    private static string FactsKey(ScopeId scopeId, string generation, string entityName, int maxDepth)
        => $"knowledge:facts:v2:{scopeId.Value:N}:{generation}:{EntitySegment(entityName)}:depth:{maxDepth}";

    /// <summary>
    /// Wires the generation key read so it resolves to <paramref name="token"/>, mirroring a
    /// generation that already exists in cache.
    /// </summary>
    private void SetUpExistingGeneration(ScopeId scopeId, string token)
    {
        var generationKey = GenerationKey(scopeId);
        _mockCacheService
            .Setup(x => x.GetAsync<KnowledgeCacheService.CachedGenerationToken>(
                generationKey,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new KnowledgeCacheService.CachedGenerationToken { Token = token });
    }

    /// <summary>
    /// Wires the generation key read to return null, mirroring a scope that has never been
    /// invalidated, or a read the cache swallowed an exception on. The service cannot tell those
    /// apart and treats both the same way: bypass the cache entirely.
    /// </summary>
    private void SetUpMissingGeneration(ScopeId scopeId)
    {
        var generationKey = GenerationKey(scopeId);
        _mockCacheService
            .Setup(x => x.GetAsync<KnowledgeCacheService.CachedGenerationToken>(
                generationKey,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((KnowledgeCacheService.CachedGenerationToken?)null);
    }

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
            }
        };
    }

    #endregion

    #region GetOrLoadRelatedFactsAsync Tests

    [Fact]
    public async Task GetOrLoadRelatedFactsAsync_CacheHit_ReturnsFacts_DoesNotCallLoad()
    {
        // Arrange
        var scopeId = ScopeId.From(Guid.NewGuid());
        var entityName = "Checkout Service";
        var maxDepth = 2;
        var facts = CreateTestFacts();
        const string generation = "abc123";
        SetUpExistingGeneration(scopeId, generation);
        var expectedKey = FactsKey(scopeId, generation, "Checkout Service", 2);

        var cachedFactList = new KnowledgeCacheService.CachedFactList { Facts = facts.ToList() };

        _mockCacheService
            .Setup(x => x.GetAsync<KnowledgeCacheService.CachedFactList>(expectedKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cachedFactList);

        var loadCallCount = 0;
        Task<IReadOnlyList<KnowledgeFactDto>> Load(CancellationToken ct)
        {
            loadCallCount++;
            return Task.FromResult((IReadOnlyList<KnowledgeFactDto>)new List<KnowledgeFactDto>());
        }

        // Act
        var result = await _sut.GetOrLoadRelatedFactsAsync(scopeId, entityName, maxDepth, Load);

        // Assert
        Assert.Equal(facts.Count, result.Count);
        for (var i = 0; i < facts.Count; i++)
        {
            Assert.Equal(facts[i].Subject, result[i].Subject);
            Assert.Equal(facts[i].Predicate, result[i].Predicate);
            Assert.Equal(facts[i].Object, result[i].Object);
        }

        Assert.Equal(0, loadCallCount);
        _mockCacheService.Verify(
            x => x.GetAsync<KnowledgeCacheService.CachedFactList>(expectedKey, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetOrLoadRelatedFactsAsync_CacheMiss_CallsLoadOnce_AndCachesNonEmptyResult()
    {
        // Arrange
        var scopeId = ScopeId.From(Guid.NewGuid());
        SetUpExistingGeneration(scopeId, "abc123");
        var entityName = "UnknownEntity";
        var maxDepth = 3;
        var facts = CreateTestFacts();
        var loadCallCount = 0;

        Task<IReadOnlyList<KnowledgeFactDto>> Load(CancellationToken ct)
        {
            loadCallCount++;
            return Task.FromResult((IReadOnlyList<KnowledgeFactDto>)facts);
        }

        // Act — Moq default returns null for unmocked GetAsync<T>, i.e. a cache miss
        var result = await _sut.GetOrLoadRelatedFactsAsync(scopeId, entityName, maxDepth, Load);

        // Assert
        Assert.Equal(facts, result);
        Assert.Equal(1, loadCallCount);
        _mockCacheService.Verify(
            x => x.SetAsync(
                It.IsAny<string>(),
                It.Is<KnowledgeCacheService.CachedFactList>(l => l.Facts.Count == facts.Count),
                It.IsAny<CacheEntryOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetOrLoadRelatedFactsAsync_CacheMiss_EmptyLoadResult_DoesNotCache()
    {
        var scopeId = ScopeId.From(Guid.NewGuid());
        SetUpExistingGeneration(scopeId, "abc123");

        Task<IReadOnlyList<KnowledgeFactDto>> Load(CancellationToken ct)
            => Task.FromResult((IReadOnlyList<KnowledgeFactDto>)new List<KnowledgeFactDto>());

        var result = await _sut.GetOrLoadRelatedFactsAsync(scopeId, "UnknownEntity", 2, Load);

        Assert.Empty(result);
        _mockCacheService.Verify(
            x => x.SetAsync(
                It.IsAny<string>(),
                It.IsAny<KnowledgeCacheService.CachedFactList>(),
                It.IsAny<CacheEntryOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetOrLoadRelatedFactsAsync_NullScopeId_ThrowsArgumentNullException()
    {
        Task<IReadOnlyList<KnowledgeFactDto>> Load(CancellationToken ct)
            => Task.FromResult((IReadOnlyList<KnowledgeFactDto>)CreateTestFacts());

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _sut.GetOrLoadRelatedFactsAsync(null!, "Checkout Service", 2, Load));
    }

    [Fact]
    public async Task GetOrLoadRelatedFactsAsync_NullEntityName_ThrowsArgumentNullException()
    {
        var scopeId = ScopeId.From(Guid.NewGuid());
        Task<IReadOnlyList<KnowledgeFactDto>> Load(CancellationToken ct)
            => Task.FromResult((IReadOnlyList<KnowledgeFactDto>)CreateTestFacts());

        // Act & Assert — ThrowIfNullOrWhiteSpace throws ArgumentNullException for null
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _sut.GetOrLoadRelatedFactsAsync(scopeId, null!, 2, Load));
    }

    [Fact]
    public async Task GetOrLoadRelatedFactsAsync_EmptyEntityName_ThrowsArgumentException()
    {
        var scopeId = ScopeId.From(Guid.NewGuid());
        Task<IReadOnlyList<KnowledgeFactDto>> Load(CancellationToken ct)
            => Task.FromResult((IReadOnlyList<KnowledgeFactDto>)CreateTestFacts());

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.GetOrLoadRelatedFactsAsync(scopeId, "", 2, Load));
    }

    [Fact]
    public async Task GetOrLoadRelatedFactsAsync_WhitespaceEntityName_ThrowsArgumentException()
    {
        var scopeId = ScopeId.From(Guid.NewGuid());
        Task<IReadOnlyList<KnowledgeFactDto>> Load(CancellationToken ct)
            => Task.FromResult((IReadOnlyList<KnowledgeFactDto>)CreateTestFacts());

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.GetOrLoadRelatedFactsAsync(scopeId, "   ", 2, Load));
    }

    [Fact]
    public async Task GetOrLoadRelatedFactsAsync_NullLoad_ThrowsArgumentNullException()
    {
        var scopeId = ScopeId.From(Guid.NewGuid());

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _sut.GetOrLoadRelatedFactsAsync(scopeId, "Checkout Service", 2, null!));
    }

    [Fact]
    public async Task GetOrLoadRelatedFactsAsync_KeyContainsScopeAndGeneration()
    {
        // The cache key must be qualified by both the scope id and its current generation token,
        // not just the entity name — that is the whole fix for cross-scope leakage.
        var scopeId = ScopeId.From(Guid.NewGuid());
        const string generation = "deadbeefdeadbeefdeadbeefdeadbeef";
        SetUpExistingGeneration(scopeId, generation);

        Task<IReadOnlyList<KnowledgeFactDto>> Load(CancellationToken ct)
            => Task.FromResult((IReadOnlyList<KnowledgeFactDto>)new List<KnowledgeFactDto>());

        await _sut.GetOrLoadRelatedFactsAsync(scopeId, "Checkout Service", 2, Load);

        var expectedKey = FactsKey(scopeId, generation, "Checkout Service", 2);
        _mockCacheService.Verify(
            x => x.GetAsync<KnowledgeCacheService.CachedFactList>(expectedKey, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetOrLoadRelatedFactsAsync_WithNoResolvableGeneration_ReadsThrough_AndTouchesNoCacheKey()
    {
        // A null generation read means either "never invalidated" or "the cache swallowed a read
        // exception", and nothing here can tell those apart. Substituting a fixed placeholder would
        // make a dropped read resolve to the same key a pre-rotation read had already written under,
        // so one transient failure could serve exactly the facts a rotation had just invalidated.
        // Bypassing the cache in both directions costs a graph traversal and cannot be wrong.
        var scopeId = ScopeId.From(Guid.NewGuid());
        SetUpMissingGeneration(scopeId);
        var facts = CreateTestFacts();
        var loadCalls = 0;

        Task<IReadOnlyList<KnowledgeFactDto>> Load(CancellationToken ct)
        {
            loadCalls++;
            return Task.FromResult((IReadOnlyList<KnowledgeFactDto>)facts);
        }

        var result = await _sut.GetOrLoadRelatedFactsAsync(scopeId, "Checkout Service", 2, Load);

        Assert.Equal(facts, result);
        Assert.Equal(1, loadCalls);

        // Nothing was written anywhere...
        _mockCacheService.Verify(
            x => x.SetAsync(
                It.IsAny<string>(),
                It.IsAny<KnowledgeCacheService.CachedFactList>(),
                It.IsAny<CacheEntryOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _mockCacheService.Verify(
            x => x.SetAsync(
                It.IsAny<string>(),
                It.IsAny<KnowledgeCacheService.CachedGenerationToken>(),
                It.IsAny<CacheEntryOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _mockCacheService.Verify(
            x => x.GetOrSetAsync(
                It.IsAny<string>(),
                It.IsAny<Func<CancellationToken, Task<KnowledgeCacheService.CachedGenerationToken>>>(),
                It.IsAny<CacheEntryOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        // ...and no facts entry was even looked for, so no placeholder key exists to collide on.
        _mockCacheService.Verify(
            x => x.GetAsync<KnowledgeCacheService.CachedFactList>(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetOrLoadRelatedFactsAsync_WithAnExistingGeneration_NeverWritesTheGenerationKey()
    {
        var scopeId = ScopeId.From(Guid.NewGuid());
        SetUpExistingGeneration(scopeId, "abc123");
        var generationKey = GenerationKey(scopeId);
        var facts = CreateTestFacts();

        Task<IReadOnlyList<KnowledgeFactDto>> Load(CancellationToken ct)
            => Task.FromResult((IReadOnlyList<KnowledgeFactDto>)facts);

        await _sut.GetOrLoadRelatedFactsAsync(scopeId, "Checkout Service", 2, Load);

        _mockCacheService.Verify(
            x => x.SetAsync(
                generationKey,
                It.IsAny<KnowledgeCacheService.CachedGenerationToken>(),
                It.IsAny<CacheEntryOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("Checkout Service", "checkout service")]
    [InlineData("Checkout Service", "checkout_service")]
    [InlineData("Checkout Service", "CHECKOUT SERVICE")]
    [InlineData("Orders API", "orders api")]
    public async Task GetOrLoadRelatedFactsAsync_EntityNamesTheGraphTreatsAsDistinct_DoNotShareACacheEntry(
        string firstName, string secondName)
    {
        // The graph matches the entity name exactly and case-sensitively (WHERE a.name = '...'), so
        // these are different entities with different facts. Lowercasing the name and turning spaces
        // into underscores — the obvious "just normalize it" move — collapses all of these onto
        // one entry, and the first lookup's facts are then served for every other spelling.
        var scopeId = ScopeId.From(Guid.NewGuid());
        const string generation = "abc123";
        SetUpExistingGeneration(scopeId, generation);

        var writtenKeys = new List<string>();
        _mockCacheService
            .Setup(x => x.SetAsync(
                It.IsAny<string>(),
                It.IsAny<KnowledgeCacheService.CachedFactList>(),
                It.IsAny<CacheEntryOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, KnowledgeCacheService.CachedFactList, CacheEntryOptions?, CancellationToken>(
                (key, _, _, _) => writtenKeys.Add(key))
            .Returns(Task.CompletedTask);

        var facts = CreateTestFacts();
        Task<IReadOnlyList<KnowledgeFactDto>> Load(CancellationToken ct)
            => Task.FromResult((IReadOnlyList<KnowledgeFactDto>)facts);

        await _sut.GetOrLoadRelatedFactsAsync(scopeId, firstName, 2, Load);
        await _sut.GetOrLoadRelatedFactsAsync(scopeId, secondName, 2, Load);

        Assert.Equal(2, writtenKeys.Count);
        Assert.NotEqual(writtenKeys[0], writtenKeys[1]);
    }

    [Fact]
    public async Task InvalidateScopeAsync_GenerationCacheOptions_AreAbsoluteAndAtLeastTwiceTheFactsTtl()
    {
        // SLIDING rather than absolute, and comfortably longer than the facts TTL. Every
        // related-facts read reads this key, which refreshes a sliding entry, so the marker lives as
        // long as the scope is actually used. Under an absolute TTL a scope that reads constantly but
        // stops WRITING facts would lose its marker, and since the read path never recreates one,
        // that scope would read through to the graph forever -- caching off permanently rather than
        // for one cold read. Not load-bearing for correctness either way; an expired marker costs
        // cache hits only. The rotation is the only writer of this key, so this is where the options
        // are applied.
        var scopeId = ScopeId.From(Guid.NewGuid());
        var generationKey = GenerationKey(scopeId);
        CacheEntryOptions? capturedOptions = null;

        _mockCacheService
            .Setup(x => x.SetAsync(
                generationKey,
                It.IsAny<KnowledgeCacheService.CachedGenerationToken>(),
                It.IsAny<CacheEntryOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, KnowledgeCacheService.CachedGenerationToken, CacheEntryOptions?, CancellationToken>(
                (_, _, options, _) => capturedOptions = options)
            .Returns(Task.CompletedTask);

        await _sut.InvalidateScopeAsync(scopeId);

        Assert.NotNull(capturedOptions);
        Assert.Null(capturedOptions!.AbsoluteExpirationRelativeToNow);
        Assert.NotNull(capturedOptions.SlidingExpiration);
        Assert.True(
            capturedOptions.SlidingExpiration >= CacheTtl.KnowledgeDocument * 2,
            $"Expected generation TTL >= {CacheTtl.KnowledgeDocument * 2}, got {capturedOptions.SlidingExpiration}");
    }

    [Fact]
    public async Task GetOrLoadRelatedFactsAsync_ResolvesGenerationOnce_BeforeLoadRuns_AndReusesItForTheWrite()
    {
        // The whole fix: the generation must be resolved exactly once per call (before `load` runs)
        // and the same value reused for the write on a miss, rather than re-resolved afterwards where
        // a concurrent rotation could have already moved it on.
        var scopeId = ScopeId.From(Guid.NewGuid());
        const string generation = "pinned-generation";
        SetUpExistingGeneration(scopeId, generation);
        var facts = CreateTestFacts();

        Task<IReadOnlyList<KnowledgeFactDto>> Load(CancellationToken ct)
            => Task.FromResult((IReadOnlyList<KnowledgeFactDto>)facts);

        await _sut.GetOrLoadRelatedFactsAsync(scopeId, "Checkout Service", 2, Load);

        var generationKey = $"knowledge:facts:v2:{scopeId.Value:N}:generation";
        var expectedFactsKey = FactsKey(scopeId, generation, "Checkout Service", 2);

        // Resolved once — a re-resolve after `load` would show up as a second read of the key.
        _mockCacheService.Verify(
            x => x.GetAsync<KnowledgeCacheService.CachedGenerationToken>(
                generationKey,
                It.IsAny<CancellationToken>()),
            Times.Once);

        // The write on the miss path used the SAME (pinned) generation as the read.
        _mockCacheService.Verify(
            x => x.SetAsync(
                expectedFactsKey,
                It.IsAny<KnowledgeCacheService.CachedFactList>(),
                It.IsAny<CacheEntryOptions?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region InvalidateScopeAsync Tests

    /// <summary>
    /// Wires <see cref="ICacheService.SetAsync{T}"/> for the generation key to actually persist what
    /// it is given, and <see cref="ICacheService.GetAsync{T}"/> for the same key to read it back —
    /// mirroring a write that lands. Without this, <see cref="ICacheService.GetAsync{T}"/> falls back
    /// to Moq's default of returning null, which every one of these tests would otherwise
    /// misinterpret as the silently-dropped-write case this class now has to distinguish.
    /// </summary>
    private void SetUpPersistedGenerationWrite(ScopeId scopeId, string? existingToken = null)
    {
        var generationKey = $"knowledge:facts:v2:{scopeId.Value:N}:generation";
        KnowledgeCacheService.CachedGenerationToken? persisted = existingToken is null
            ? null
            : new KnowledgeCacheService.CachedGenerationToken { Token = existingToken };

        _mockCacheService
            .Setup(x => x.SetAsync(
                generationKey,
                It.IsAny<KnowledgeCacheService.CachedGenerationToken>(),
                It.IsAny<CacheEntryOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, KnowledgeCacheService.CachedGenerationToken, CacheEntryOptions?, CancellationToken>(
                (_, token, _, _) => persisted = token)
            .Returns(Task.CompletedTask);

        _mockCacheService
            .Setup(x => x.GetAsync<KnowledgeCacheService.CachedGenerationToken>(generationKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => persisted);
    }

    /// <summary>
    /// Verifies whether <paramref name="logger"/> logged at <paramref name="level"/> at least once,
    /// via Moq's recorded invocations against <see cref="ILogger.Log"/>.
    /// </summary>
    private static bool LoggedAt(Mock<ILogger<KnowledgeCacheService>> logger, LogLevel level) =>
        logger.Invocations.Any(i => i.Method.Name == nameof(ILogger.Log)
                                     && i.Arguments.Count > 0
                                     && i.Arguments[0] is LogLevel loggedLevel
                                     && loggedLevel == level);

    /// <summary>
    /// Formatted text of every message <paramref name="logger"/> logged at <paramref name="level"/>.
    /// </summary>
    private static IReadOnlyList<string> MessagesAt(Mock<ILogger<KnowledgeCacheService>> logger, LogLevel level) =>
        logger.Invocations
            .Where(i => i.Method.Name == nameof(ILogger.Log)
                        && i.Arguments.Count > 2
                        && i.Arguments[0] is LogLevel loggedLevel
                        && loggedLevel == level)
            .Select(i => i.Arguments[2]?.ToString() ?? string.Empty)
            .ToList();

    [Fact]
    public async Task InvalidateScopeAsync_StoresANewGenerationToken()
    {
        var scopeId = ScopeId.From(Guid.NewGuid());
        SetUpPersistedGenerationWrite(scopeId);
        KnowledgeCacheService.CachedGenerationToken? capturedToken = null;
        string? capturedKey = null;

        _mockCacheService
            .Setup(x => x.SetAsync<KnowledgeCacheService.CachedGenerationToken>(
                It.IsAny<string>(),
                It.IsAny<KnowledgeCacheService.CachedGenerationToken>(),
                It.IsAny<CacheEntryOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, KnowledgeCacheService.CachedGenerationToken, CacheEntryOptions?, CancellationToken>(
                (key, token, _, _) => { capturedKey = key; capturedToken = token; })
            .Returns(Task.CompletedTask);

        await _sut.InvalidateScopeAsync(scopeId);

        Assert.Equal($"knowledge:facts:v2:{scopeId.Value:N}:generation", capturedKey);
        Assert.NotNull(capturedToken);
        Assert.False(string.IsNullOrWhiteSpace(capturedToken!.Token));
    }

    [Fact]
    public async Task InvalidateScopeAsync_ReplacesTheExistingTokenInTheCache()
    {
        // The whole point of rotation: the token a scope's facts were filed under before the
        // invalidation must not still be the one readers resolve afterwards. Seed a real existing
        // token and assert on what the cache actually holds at the end, rather than only on what was
        // handed to SetAsync — a write that never lands would still satisfy the latter.
        var scopeId = ScopeId.From(Guid.NewGuid());
        const string oldToken = "old-token-value";
        SetUpPersistedGenerationWrite(scopeId, oldToken);

        await _sut.InvalidateScopeAsync(scopeId);

        var stored = await _mockCacheService.Object.GetAsync<KnowledgeCacheService.CachedGenerationToken>(
            GenerationKey(scopeId),
            CancellationToken.None);

        Assert.NotNull(stored);
        Assert.NotEqual(oldToken, stored!.Token);
        Assert.False(string.IsNullOrWhiteSpace(stored.Token));
        Assert.False(LoggedAt(_mockLogger, LogLevel.Error));
    }

    [Fact]
    public async Task InvalidateScopeAsync_WhenThePreRotationValueWasUnreadable_WarnsWithoutClaimingEitherOutcome()
    {
        // GetAsync returns null both for a genuine miss and for a read the cache swallowed an
        // exception on, so a null pre-read leaves no baseline to compare the read-back against. The
        // key ends up holding a token that is neither ours nor anything we read, which is EITHER a
        // concurrent rotation that won (fine) OR a dropped write over a stale token (not fine), and
        // nothing here can tell those apart. Say so, rather than asserting either outcome: claiming
        // success would hide a staleness incident, and claiming failure would fire on routine
        // concurrency until people stopped reading the alarm.
        var scopeId = ScopeId.From(Guid.NewGuid());
        var generationKey = GenerationKey(scopeId);
        var someoneElsesToken = new KnowledgeCacheService.CachedGenerationToken { Token = "not-ours" };

        _mockCacheService
            .SetupSequence(x => x.GetAsync<KnowledgeCacheService.CachedGenerationToken>(generationKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync((KnowledgeCacheService.CachedGenerationToken?)null)
            .ReturnsAsync(someoneElsesToken)
            // Third read: the fail-closed removal is verified, and this double has
            // nothing left under the key afterwards.
            .ReturnsAsync((KnowledgeCacheService.CachedGenerationToken?)null);

        await _sut.InvalidateScopeAsync(scopeId);

        Assert.True(LoggedAt(_mockLogger, LogLevel.Warning));
        Assert.False(LoggedAt(_mockLogger, LogLevel.Error));
        Assert.DoesNotContain(
            MessagesAt(_mockLogger, LogLevel.Debug),
            m => m.Contains("Rotated knowledge cache generation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidateScopeAsync_WhenTheRotationCannotBeConfirmed_RemovesTheGenerationMarker()
    {
        // The unconfirmable case is EITHER a concurrent rotation (benign) OR a dropped write that
        // left the pre-rotation token in place with its entries still reachable (not benign), and
        // nothing here can tell which. Dropping the marker resolves that in the safe direction: a
        // reader that finds no generation bypasses the cache, so nothing stale can be served either
        // way. It costs the benign case its cache until the next write, which is the cheaper mistake.
        var scopeId = ScopeId.From(Guid.NewGuid());
        var generationKey = GenerationKey(scopeId);
        var somethingElse = new KnowledgeCacheService.CachedGenerationToken { Token = "not-ours" };

        _mockCacheService
            .SetupSequence(x => x.GetAsync<KnowledgeCacheService.CachedGenerationToken>(generationKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync((KnowledgeCacheService.CachedGenerationToken?)null)
            .ReturnsAsync(somethingElse)
            // Third read: the fail-closed removal is verified, and this double has
            // nothing left under the key afterwards.
            .ReturnsAsync((KnowledgeCacheService.CachedGenerationToken?)null);

        await _sut.InvalidateScopeAsync(scopeId);

        _mockCacheService.Verify(
            x => x.RemoveAsync(generationKey, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task InvalidateScopeAsync_WhenBothReadsAreSwallowed_StillRemovesTheGenerationMarker()
    {
        // The worst shape of the ambiguity: the pre-read is swallowed, the write is silently
        // dropped, and the read-back is swallowed too. From here that is indistinguishable from a
        // scope that simply has no generation -- but if the key actually still holds a pre-rotation
        // token, its entries are reachable and stale. Removing the marker covers both readings: a
        // reader that finds no generation bypasses the cache, so nothing stale can be served.
        var scopeId = ScopeId.From(Guid.NewGuid());
        var generationKey = GenerationKey(scopeId);

        _mockCacheService
            .Setup(x => x.GetAsync<KnowledgeCacheService.CachedGenerationToken>(generationKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync((KnowledgeCacheService.CachedGenerationToken?)null);

        await _sut.InvalidateScopeAsync(scopeId);

        _mockCacheService.Verify(
            x => x.RemoveAsync(generationKey, It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.DoesNotContain(
            MessagesAt(_mockLogger, LogLevel.Debug),
            m => m.Contains("Rotated knowledge cache generation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidateScopeAsync_WhenEveryReadIsSwallowedAndAStaleMarkerSurvives_LogsAnError()
    {
        // No baseline at all: pre-read swallowed, write dropped, read-back swallowed, removal dropped,
        // and a stale token still sitting there. Nothing in this call ever read a token, so nothing
        // can prove the survivor is new -- and treating "cannot prove" as "probably fine" would leave
        // readers resolving it.
        var scopeId = ScopeId.From(Guid.NewGuid());
        var generationKey = GenerationKey(scopeId);
        var stale = new KnowledgeCacheService.CachedGenerationToken { Token = "stale-and-still-there" };

        _mockCacheService
            .SetupSequence(x => x.GetAsync<KnowledgeCacheService.CachedGenerationToken>(generationKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync((KnowledgeCacheService.CachedGenerationToken?)null)
            .ReturnsAsync((KnowledgeCacheService.CachedGenerationToken?)null)
            .ReturnsAsync(stale);

        await _sut.InvalidateScopeAsync(scopeId);

        Assert.Contains(
            MessagesAt(_mockLogger, LogLevel.Error),
            m => m.Contains("could not be completed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidateScopeAsync_WhenAConcurrentRotationWritesAfterTheRemoval_DoesNotReportAFailure()
    {
        // This call's own write went missing -- the read-back still showed the pre-rotation token --
        // but by the time the marker was re-read, another rotation had written a fresh generation.
        // The old generation is unreachable either way, which is the entire job, so this is a success
        // however little of it this call did. Reporting it as a failed rotation would fire an Error
        // during exactly the concurrent-write traffic the rest of this method treats as routine.
        var scopeId = ScopeId.From(Guid.NewGuid());
        var generationKey = GenerationKey(scopeId);
        var beforeRotation = new KnowledgeCacheService.CachedGenerationToken { Token = "before-rotation" };
        var writtenByAnother = new KnowledgeCacheService.CachedGenerationToken { Token = "written-by-another-rotation" };

        _mockCacheService
            .SetupSequence(x => x.GetAsync<KnowledgeCacheService.CachedGenerationToken>(generationKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(beforeRotation)     // pre-rotation read
            .ReturnsAsync(beforeRotation)     // read-back: this call's write was lost
            .ReturnsAsync(writtenByAnother);  // after the removal: somebody else's fresh generation

        await _sut.InvalidateScopeAsync(scopeId);

        Assert.False(LoggedAt(_mockLogger, LogLevel.Error));
        Assert.DoesNotContain(
            MessagesAt(_mockLogger, LogLevel.Error),
            m => m.Contains("did not take effect", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidateScopeAsync_WhenTheRotationIsConfirmed_LeavesTheGenerationMarkerInPlace()
    {
        var scopeId = ScopeId.From(Guid.NewGuid());
        var generationKey = GenerationKey(scopeId);
        SetUpPersistedGenerationWrite(scopeId, "before-rotation");

        await _sut.InvalidateScopeAsync(scopeId);

        _mockCacheService.Verify(
            x => x.RemoveAsync(generationKey, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task InvalidateScopeAsync_WhenTwoFirstTimeRotationsRace_DoesNotLogAnError()
    {
        // Both rotations find the key absent, so both pre-reads are legitimately null. If the other
        // one wins, this call's read-back shows a token that is not its own -- but the old generation
        // is unreachable either way, so nothing was lost. Several fact writes run concurrently for one
        // scope and the first turn races on a key that does not exist yet, so an Error here would fire
        // on ordinary traffic.
        var scopeId = ScopeId.From(Guid.NewGuid());
        var generationKey = GenerationKey(scopeId);
        var theOtherRotation = new KnowledgeCacheService.CachedGenerationToken { Token = "written-by-the-other-rotation" };

        _mockCacheService
            .SetupSequence(x => x.GetAsync<KnowledgeCacheService.CachedGenerationToken>(generationKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync((KnowledgeCacheService.CachedGenerationToken?)null)
            .ReturnsAsync(theOtherRotation)
            // Third read: the fail-closed removal is verified, and this double has
            // nothing left under the key afterwards.
            .ReturnsAsync((KnowledgeCacheService.CachedGenerationToken?)null);

        await _sut.InvalidateScopeAsync(scopeId);

        Assert.False(LoggedAt(_mockLogger, LogLevel.Error));
    }

    [Fact]
    public async Task InvalidateScopeAsync_WhenAConcurrentRotationWins_DoesNotReportALostInvalidation()
    {
        // A caller writing several facts for one scope at once produces overlapping rotations.
        // Whichever token ends up stored, the pre-rotation generation is unreachable and invalidation
        // has succeeded — comparing the read-back against THIS call's own token would log an error
        // on every one of those crossings.
        var scopeId = ScopeId.From(Guid.NewGuid());
        var generationKey = GenerationKey(scopeId);
        var beforeRotation = new KnowledgeCacheService.CachedGenerationToken { Token = "before-rotation" };
        var concurrentWinner = new KnowledgeCacheService.CachedGenerationToken { Token = "written-by-another-rotation" };

        // First read (the pre-rotation capture) sees the old token; the read-back sees a different
        // token than this call wrote, because a concurrent rotation landed last.
        _mockCacheService
            .SetupSequence(x => x.GetAsync<KnowledgeCacheService.CachedGenerationToken>(generationKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(beforeRotation)
            .ReturnsAsync(concurrentWinner)
            // Third read: the fail-closed removal is verified, and this double has
            // nothing left under the key afterwards.
            .ReturnsAsync((KnowledgeCacheService.CachedGenerationToken?)null);

        await _sut.InvalidateScopeAsync(scopeId);

        Assert.False(LoggedAt(_mockLogger, LogLevel.Error));
    }

    [Fact]
    public async Task InvalidateScopeAsync_NullScopeId_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _sut.InvalidateScopeAsync(null!));
    }

    [Fact]
    public async Task InvalidateScopeAsync_WhenTheCacheSilentlyDropsTheWrite_LogsAnError()
    {
        // The production Redis-backed ICacheService implementation swallows every exception, so a
        // cache blip makes the write here a no-op without throwing. Leave SetAsync unconfigured
        // (Moq's default no-op that still returns Task.CompletedTask) and have GetAsync read back an
        // OLD token, exactly as a dropped write would look from here.
        var scopeId = ScopeId.From(Guid.NewGuid());
        var generationKey = $"knowledge:facts:v2:{scopeId.Value:N}:generation";
        var oldToken = new KnowledgeCacheService.CachedGenerationToken { Token = "old-token-that-never-got-replaced" };

        _mockCacheService
            .Setup(x => x.GetAsync<KnowledgeCacheService.CachedGenerationToken>(generationKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(oldToken);

        await _sut.InvalidateScopeAsync(scopeId);

        Assert.True(LoggedAt(_mockLogger, LogLevel.Error));
        Assert.DoesNotContain(
            MessagesAt(_mockLogger, LogLevel.Debug),
            m => m.Contains("Rotated knowledge cache generation", StringComparison.Ordinal));

        // A rotation known to have failed fails closed too: the marker goes, so readers bypass the
        // cache rather than resolving the pre-rotation token and serving its entries.
        _mockCacheService.Verify(
            x => x.RemoveAsync(generationKey, It.IsAny<CancellationToken>()),
            Times.Once);

        // This double is a cache that drops EVERY write, so the removal is dropped too and the marker
        // survives -- the one case where entries really do stay reachable. It must say so.
        Assert.Contains(
            MessagesAt(_mockLogger, LogLevel.Error),
            m => m.Contains("could not be completed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidateScopeAsync_WhenTheWriteIsDroppedButTheRemovalLands_ReportsAFailedRotationNotAStaleCache()
    {
        // The write is lost, so the key still holds its pre-rotation token -- but the fail-closed
        // removal succeeds, so nothing stale stays reachable. That is a failed invalidation worth an
        // Error, and specifically NOT the "marker could not be dropped" one.
        var scopeId = ScopeId.From(Guid.NewGuid());
        var generationKey = GenerationKey(scopeId);
        var oldToken = new KnowledgeCacheService.CachedGenerationToken { Token = "never-replaced" };

        _mockCacheService
            .SetupSequence(x => x.GetAsync<KnowledgeCacheService.CachedGenerationToken>(generationKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(oldToken)                                              // pre-rotation read
            .ReturnsAsync(oldToken)                                              // read-back: unchanged
            .ReturnsAsync((KnowledgeCacheService.CachedGenerationToken?)null);   // after removal: gone

        await _sut.InvalidateScopeAsync(scopeId);

        Assert.Contains(
            MessagesAt(_mockLogger, LogLevel.Error),
            m => m.Contains("did not take effect", StringComparison.Ordinal));
        Assert.DoesNotContain(
            MessagesAt(_mockLogger, LogLevel.Error),
            m => m.Contains("could not be completed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidateScopeAsync_WhenAnotherRotationWritesAfterTheRemoval_DoesNotReportAStaleCache()
    {
        // A token present after the removal is only a problem if it is the SAME one that was there
        // before. A different token means another rotation wrote after this removal, which is a fresh
        // generation with nothing stale under it.
        var scopeId = ScopeId.From(Guid.NewGuid());
        var generationKey = GenerationKey(scopeId);
        var oldToken = new KnowledgeCacheService.CachedGenerationToken { Token = "never-replaced" };
        var somebodyElse = new KnowledgeCacheService.CachedGenerationToken { Token = "written-after-the-removal" };

        _mockCacheService
            .SetupSequence(x => x.GetAsync<KnowledgeCacheService.CachedGenerationToken>(generationKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(oldToken)
            .ReturnsAsync(oldToken)
            .ReturnsAsync(somebodyElse);

        await _sut.InvalidateScopeAsync(scopeId);

        Assert.DoesNotContain(
            MessagesAt(_mockLogger, LogLevel.Error),
            m => m.Contains("could not be completed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidateScopeAsync_WhenTheWritePersists_LogsNoError()
    {
        var scopeId = ScopeId.From(Guid.NewGuid());
        SetUpPersistedGenerationWrite(scopeId);

        await _sut.InvalidateScopeAsync(scopeId);

        Assert.False(LoggedAt(_mockLogger, LogLevel.Error));
    }

    #endregion
}
