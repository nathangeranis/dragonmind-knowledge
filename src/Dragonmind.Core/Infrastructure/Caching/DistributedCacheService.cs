using System.Collections.Concurrent;
using System.Text.Json;

using Dragonmind.Core.Application.Caching;

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Dragonmind.Core.Infrastructure.Caching;

/// <summary>
/// <see cref="IDistributedCache"/>-backed implementation of <see cref="ICacheService"/>.
/// Works with any <see cref="IDistributedCache"/> provider — Redis in production, an
/// in-memory distributed cache in tests or local development — layering type-safe JSON
/// serialization and stampede protection on top.
/// </summary>
public sealed class DistributedCacheService : ICacheService
{
    private readonly IDistributedCache _cache;
    private readonly ILogger<DistributedCacheService> _logger;

    // Thread-safe dictionary for preventing cache stampede on GetOrSetAsync
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new Domain.SharedIdentities.StronglyTypedIdJsonConverterFactory() }
    };

    public DistributedCacheService(IDistributedCache cache, ILogger<DistributedCacheService> logger)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        try
        {
            var cached = await _cache.GetStringAsync(key, cancellationToken);

            if (string.IsNullOrWhiteSpace(cached))
            {
                return null;
            }

            var result = JsonSerializer.Deserialize<T>(cached, _jsonOptions);
            _logger.LogDebug("Cache HIT for key {CacheKey}", key);
            return result;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Cache deserialization failed for key {CacheKey}, invalidating entry", key);
            await RemoveAsync(key, cancellationToken);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cache read failed for key {CacheKey}", key);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, CacheEntryOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        try
        {
            var json = JsonSerializer.Serialize(value, _jsonOptions);
            var distributedOptions = MapToDistributedCacheOptions(options);

            await _cache.SetStringAsync(key, json, distributedOptions, cancellationToken);
            _logger.LogDebug("Cache SET for key {CacheKey}", key);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Cache serialization failed for key {CacheKey}", key);
            // Don't rethrow - caching is not critical
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cache write failed for key {CacheKey}", key);
            // Don't rethrow - caching is not critical
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        try
        {
            await _cache.RemoveAsync(key, cancellationToken);
            _logger.LogDebug("Cache REMOVE for key {CacheKey}", key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cache remove failed for key {CacheKey}", key);
            // Don't rethrow - caching is not critical
        }
    }

    /// <inheritdoc />
    public async Task<T> GetOrSetAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CacheEntryOptions? options = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);

        // Try to get from cache first
        var cached = await GetAsync<T>(key, cancellationToken);
        if (cached != null)
        {
            return cached;
        }

        // Cache miss - use lock to prevent cache stampede
        var lockObj = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        await lockObj.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring lock
            cached = await GetAsync<T>(key, cancellationToken);
            if (cached != null)
            {
                return cached;
            }

            // Execute factory and cache result
            _logger.LogDebug("Cache MISS for key {CacheKey}, executing factory", key);
            var value = await factory(cancellationToken);

            if (value != null)
            {
                await SetAsync(key, value, options, cancellationToken);
            }

            return value!; // Factory is expected to return non-null; null-check above handles edge case
        }
        finally
        {
            lockObj.Release();

            // Clean up unused locks to prevent memory leak
            // Only remove if no one is waiting
            if (lockObj.CurrentCount == 1)
            {
                _locks.TryRemove(key, out _);
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        try
        {
            var cached = await _cache.GetStringAsync(key, cancellationToken);
            return !string.IsNullOrWhiteSpace(cached);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cache exists check failed for key {CacheKey}", key);
            return false;
        }
    }

    /// <summary>
    /// Maps CacheEntryOptions to DistributedCacheEntryOptions.
    /// </summary>
    private static DistributedCacheEntryOptions MapToDistributedCacheOptions(CacheEntryOptions? options)
    {
        if (options == null)
        {
            return new DistributedCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(30) // Default
            };
        }

        var distributedOptions = new DistributedCacheEntryOptions();

        if (options.SlidingExpiration.HasValue)
        {
            distributedOptions.SlidingExpiration = options.SlidingExpiration.Value;
        }

        if (options.AbsoluteExpirationRelativeToNow.HasValue)
        {
            distributedOptions.AbsoluteExpirationRelativeToNow = options.AbsoluteExpirationRelativeToNow.Value;
        }

        if (options.AbsoluteExpiration.HasValue)
        {
            distributedOptions.AbsoluteExpiration = options.AbsoluteExpiration.Value;
        }

        // If no expiration set, apply default
        if (!distributedOptions.SlidingExpiration.HasValue &&
            !distributedOptions.AbsoluteExpirationRelativeToNow.HasValue &&
            !distributedOptions.AbsoluteExpiration.HasValue)
        {
            distributedOptions.SlidingExpiration = TimeSpan.FromMinutes(30);
        }

        return distributedOptions;
    }
}
