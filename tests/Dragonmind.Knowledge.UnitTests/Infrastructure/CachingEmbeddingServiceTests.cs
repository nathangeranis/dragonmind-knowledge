using Dragonmind.Knowledge.Domain.DomainServices;
using Dragonmind.Knowledge.Domain.ValueObjects;
using Dragonmind.Knowledge.Infrastructure.Caching;
using Dragonmind.Knowledge.Infrastructure.Embeddings;

using Moq;

namespace Dragonmind.Knowledge.UnitTests.Infrastructure;

/// <summary>
/// Unit tests for <see cref="CachingEmbeddingService"/>, the cache-aside decorator over
/// <see cref="IEmbeddingService"/>. Verifies that a cache miss delegates to the inner service
/// exactly once, a cache hit is served without touching the inner service, and the model name
/// is required for the cache key.
/// </summary>
public class CachingEmbeddingServiceTests
{
    private const string ModelName = "text-embedding-3-small";

    private static CachingEmbeddingService CreateSut(
        Mock<IEmbeddingService> inner,
        Mock<IEmbeddingCacheService> cache)
        => new(inner.Object, cache.Object, ModelName);

    [Fact]
    public async Task GenerateEmbeddingAsync_CacheMiss_GeneratesOnceAndReturnsVector()
    {
        // Arrange — GetOrSetAsync simulates a miss by invoking the supplied factory.
        var generatedVector = new float[] { 0.1f, 0.2f, 0.3f };
        var inner = new Mock<IEmbeddingService>();
        inner.Setup(s => s.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(Embedding.Create(generatedVector));

        var cache = new Mock<IEmbeddingCacheService>();
        cache.Setup(c => c.GetOrSetAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Func<CancellationToken, Task<float[]>>>(),
                It.IsAny<CancellationToken>()))
             .Returns((string _, string _, Func<CancellationToken, Task<float[]>> factory, CancellationToken ct) => factory(ct));

        var sut = CreateSut(inner, cache);

        // Act
        var result = await sut.GenerateEmbeddingAsync("The Checkout Service retries failed payments.");

        // Assert
        Assert.Equal(generatedVector, result.Vector);
        inner.Verify(s => s.GenerateEmbeddingAsync("The Checkout Service retries failed payments.", It.IsAny<CancellationToken>()), Times.Once);
        cache.Verify(c => c.GetOrSetAsync(
                "The Checkout Service retries failed payments.",
                ModelName,
                It.IsAny<Func<CancellationToken, Task<float[]>>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_CacheHit_ReturnsCachedVectorWithoutGenerating()
    {
        // Arrange — GetOrSetAsync returns a cached vector without invoking the factory.
        var cachedVector = new float[] { 0.9f, 0.8f, 0.7f };
        var inner = new Mock<IEmbeddingService>();

        var cache = new Mock<IEmbeddingCacheService>();
        cache.Setup(c => c.GetOrSetAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Func<CancellationToken, Task<float[]>>>(),
                It.IsAny<CancellationToken>()))
             .ReturnsAsync(cachedVector);

        var sut = CreateSut(inner, cache);

        // Act
        var result = await sut.GenerateEmbeddingAsync("The Checkout Service retries failed payments.");

        // Assert — served from cache; inner generator never invoked.
        Assert.Equal(cachedVector, result.Vector);
        inner.Verify(s => s.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_BlankModelName_ThrowsArgumentException(string? modelName)
    {
        var inner = new Mock<IEmbeddingService>();
        var cache = new Mock<IEmbeddingCacheService>();

        Assert.Throws<ArgumentException>(
            () => new CachingEmbeddingService(inner.Object, cache.Object, modelName!));
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_NullText_ThrowsAndNeverHitsCache()
    {
        var inner = new Mock<IEmbeddingService>();
        var cache = new Mock<IEmbeddingCacheService>();
        var sut = CreateSut(inner, cache);

        await Assert.ThrowsAsync<ArgumentNullException>(() => sut.GenerateEmbeddingAsync(null!));

        cache.Verify(c => c.GetOrSetAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<Func<CancellationToken, Task<float[]>>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
