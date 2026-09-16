namespace Dragonmind.Core.Application.Caching;

/// <summary>
/// Generic cache service interface providing typed cache operations.
/// Abstracts the underlying cache implementation (Redis, memory, etc.)
/// for use by bounded context facades and services.
/// </summary>
public interface ICacheService
{
    /// <summary>
    /// Gets a cached value by key.
    /// </summary>
    /// <typeparam name="T">The type of the cached value.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cached value, or null if not found or deserialization fails.</returns>
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Sets a value in the cache.
    /// </summary>
    /// <typeparam name="T">The type of the value to cache.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="value">The value to cache.</param>
    /// <param name="options">Cache entry options (expiration, etc.).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetAsync<T>(string key, T value, CacheEntryOptions? options = null, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Removes a value from the cache.
    /// </summary>
    /// <param name="key">The cache key to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a cached value or creates it using the factory if not found.
    /// Thread-safe: only one factory call will execute for concurrent requests with the same key.
    /// </summary>
    /// <typeparam name="T">The type of the cached value.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="factory">Factory function to create the value if not cached.</param>
    /// <param name="options">Cache entry options (expiration, etc.).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cached or newly created value.</returns>
    Task<T> GetOrSetAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CacheEntryOptions? options = null, CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Checks if a key exists in the cache.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the key exists, false otherwise.</returns>
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

}

/// <summary>
/// Options for cache entry expiration and behavior.
/// </summary>
public sealed class CacheEntryOptions
{
    /// <summary>
    /// Sliding expiration - the cache entry expires if not accessed within this time.
    /// Each access resets the timer.
    /// </summary>
    public TimeSpan? SlidingExpiration { get; init; }

    /// <summary>
    /// Absolute expiration relative to now — the cache entry expires after this duration regardless of access.
    /// </summary>
    public TimeSpan? AbsoluteExpirationRelativeToNow { get; init; }

    /// <summary>
    /// Absolute expiration at a specific date/time.
    /// </summary>
    public DateTimeOffset? AbsoluteExpiration { get; init; }

    /// <summary>
    /// Creates options with sliding expiration.
    /// </summary>
    public static CacheEntryOptions Sliding(TimeSpan duration) => new() { SlidingExpiration = duration };

    /// <summary>
    /// Creates options with absolute expiration relative to now.
    /// </summary>
    public static CacheEntryOptions Absolute(TimeSpan duration) => new() { AbsoluteExpirationRelativeToNow = duration };

    /// <summary>
    /// Creates options with both sliding and absolute expiration.
    /// The entry expires at whichever comes first.
    /// </summary>
    public static CacheEntryOptions SlidingWithMax(TimeSpan sliding, TimeSpan absolute) => new()
    {
        SlidingExpiration = sliding,
        AbsoluteExpirationRelativeToNow = absolute
    };
}
