using Dragonmind.Core.Application.Caching;

using Microsoft.Extensions.Logging;

namespace Dragonmind.Knowledge.Infrastructure.Caching;

/// <summary>
/// Cache service for AI-generated embeddings.
/// Embeddings are deterministic given the same input and model,
/// so they can be cached with very long TTL (1 year).
/// </summary>
public interface IEmbeddingCacheService
{
    /// <summary>
    /// Gets a cached embedding for the given query and model.
    /// </summary>
    /// <param name="query">The text that was embedded.</param>
    /// <param name="modelName">The embedding model name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cached embedding vector, or null if not found.</returns>
    Task<float[]?> GetAsync(
        string query,
        string modelName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Caches an embedding for the given query and model.
    /// </summary>
    /// <param name="query">The text that was embedded.</param>
    /// <param name="modelName">The embedding model name.</param>
    /// <param name="embedding">The embedding vector to cache.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetAsync(
        string query,
        string modelName,
        float[] embedding,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a cached embedding or generates it using the factory if not found.
    /// Thread-safe: only one factory call will execute for concurrent requests.
    /// </summary>
    /// <param name="query">The text to embed.</param>
    /// <param name="modelName">The embedding model name.</param>
    /// <param name="factory">Factory function to generate the embedding if not cached.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cached or newly generated embedding vector.</returns>
    Task<float[]> GetOrSetAsync(
        string query,
        string modelName,
        Func<CancellationToken, Task<float[]>> factory,
        CancellationToken cancellationToken = default);

}

/// <summary>
/// Implementation of embedding caching using ICacheService.
/// Uses 1-year absolute expiration since embeddings are deterministic.
/// </summary>
public class EmbeddingCacheService : IEmbeddingCacheService
{
    private const string CacheKeyPrefix = "embedding";

    private readonly ICacheService _cacheService;
    private readonly ILogger<EmbeddingCacheService> _logger;
    private readonly CacheEntryOptions _cacheOptions;

    public EmbeddingCacheService(
        ICacheService cacheService,
        ILogger<EmbeddingCacheService> logger)
    {
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cacheOptions = CacheTtl.EmbeddingsOptions; // 1-year absolute expiration
    }

    /// <inheritdoc />
    public async Task<float[]?> GetAsync(
        string query,
        string modelName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var key = GetCacheKey(query, modelName);
        var cached = await _cacheService.GetAsync<EmbeddingWrapper>(key, cancellationToken);

        if (cached != null)
        {
            _logger.LogDebug("Embedding cache HIT for model {ModelName}, query length {QueryLength}",
                modelName, query.Length);
            return cached.Values;
        }

        _logger.LogDebug("Embedding cache MISS for model {ModelName}, query length {QueryLength}",
            modelName, query.Length);
        return null;
    }

    /// <inheritdoc />
    public async Task SetAsync(
        string query,
        string modelName,
        float[] embedding,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentNullException.ThrowIfNull(embedding);

        if (embedding.Length == 0)
        {
            _logger.LogWarning("Attempted to cache empty embedding for model {ModelName}", modelName);
            return;
        }

        var key = GetCacheKey(query, modelName);
        var wrapper = new EmbeddingWrapper { Values = embedding };
        await _cacheService.SetAsync(key, wrapper, _cacheOptions, cancellationToken);

        _logger.LogDebug("Cached embedding ({Dimensions} dimensions) for model {ModelName}",
            embedding.Length, modelName);
    }

    /// <inheritdoc />
    public async Task<float[]> GetOrSetAsync(
        string query,
        string modelName,
        Func<CancellationToken, Task<float[]>> factory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentNullException.ThrowIfNull(factory);

        var key = GetCacheKey(query, modelName);

        var wrapper = await _cacheService.GetOrSetAsync(
            key,
            async ct =>
            {
                _logger.LogDebug("Generating embedding for model {ModelName}, query length {QueryLength}",
                    modelName, query.Length);
                var embedding = await factory(ct);
                return new EmbeddingWrapper { Values = embedding };
            },
            _cacheOptions,
            cancellationToken);

        return wrapper.Values;
    }

    #region Cache Key Generation

    private static string GetCacheKey(string query, string modelName)
    {
        // Key format: embedding:{model}:{hash}
        // We use a hash of the query to avoid very long cache keys
        var queryHash = ComputeQueryHash(query);
        return $"{CacheKeyPrefix}:{NormalizeModelName(modelName)}:{queryHash}";
    }

    private static string NormalizeModelName(string modelName)
    {
        // Normalize model name for cache key (lowercase, replace special chars)
        return modelName.ToLowerInvariant()
            .Replace('/', '_')
            .Replace(':', '_')
            .Replace(' ', '_');
    }

    private static string ComputeQueryHash(string query)
    {
        // Use SHA256 hash for consistent, collision-resistant key generation
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(query);
        var hash = sha256.ComputeHash(bytes);
        // Use first 16 bytes (32 hex chars) for reasonable key length
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    #endregion

    #region Cache Wrapper Classes

    /// <summary>
    /// Wrapper class for caching float arrays.
    /// Required because ICacheService.GetAsync expects a class type.
    /// </summary>
    private sealed class EmbeddingWrapper
    {
        public float[] Values { get; init; } = Array.Empty<float>();
    }

    #endregion
}
