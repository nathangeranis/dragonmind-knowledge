using Dragonmind.Knowledge.Domain.DomainServices;
using Dragonmind.Knowledge.Domain.ValueObjects;
using Dragonmind.Knowledge.Infrastructure.Caching;

namespace Dragonmind.Knowledge.Infrastructure.Embeddings;

/// <summary>
/// Caching decorator over <see cref="IEmbeddingService"/>. Embeddings are deterministic for a
/// given (text, model) pair, so query/document embeddings are served from
/// <see cref="IEmbeddingCacheService"/> (1-year absolute TTL, stampede-protected) instead of
/// issuing a live model call for every identical request. Mirrors the existing
/// <c>Caching*ContextFacade</c> decorator convention. Cache HIT/MISS logging lives in
/// <c>EmbeddingCacheService</c>, so this decorator adds none of its own.
/// </summary>
public sealed class CachingEmbeddingService : IEmbeddingService
{
    private readonly IEmbeddingService _inner;
    private readonly IEmbeddingCacheService _cache;
    private readonly string _modelName;

    /// <summary>
    /// Initializes a new instance of <see cref="CachingEmbeddingService"/>.
    /// </summary>
    /// <param name="inner">The concrete embedding service that performs generation on a cache miss.</param>
    /// <param name="cache">The embedding cache (keyed by text + model name).</param>
    /// <param name="modelName">
    /// The embedding model name — part of the cache key so a model change yields distinct keys.
    /// Must be non-empty; <see cref="IEmbeddingCacheService.GetOrSetAsync"/> rejects blank model names.
    /// </param>
    public CachingEmbeddingService(
        IEmbeddingService inner,
        IEmbeddingCacheService cache,
        string modelName)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _modelName = string.IsNullOrWhiteSpace(modelName)
            ? throw new ArgumentException("A non-empty embedding model name is required for cache keys.", nameof(modelName))
            : modelName;
    }

    /// <inheritdoc />
    public async Task<Embedding> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text, nameof(text));

        // Cache-aside via GetOrSetAsync: on a miss, the inner service generates and the vector is
        // cached; concurrent identical requests share a single generation (stampede protection).
        var vector = await _cache.GetOrSetAsync(
            text,
            _modelName,
            async ct => (await _inner.GenerateEmbeddingAsync(text, ct)).Vector,
            cancellationToken);

        return Embedding.Create(vector);
    }
}
