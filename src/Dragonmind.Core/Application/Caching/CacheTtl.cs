namespace Dragonmind.Core.Application.Caching;

/// <summary>
/// Centralized cache TTL (Time-To-Live) configuration for the Knowledge context.
/// These values represent recommended defaults based on data volatility and access patterns.
/// </summary>
public static class CacheTtl
{
    #region Knowledge Documents

    /// <summary>
    /// Knowledge document cache TTL. Medium-lived for stored knowledge content.
    /// </summary>
    public static readonly TimeSpan KnowledgeDocument = TimeSpan.FromHours(2);

    #endregion

    #region Embeddings

    /// <summary>
    /// Embedding cache TTL. Very long-lived since embeddings are deterministic.
    /// Absolute expiration with yearly refresh to prevent unbounded growth.
    /// </summary>
    public static readonly TimeSpan Embeddings = TimeSpan.FromDays(365);

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates CacheEntryOptions with sliding expiration using the specified TTL.
    /// </summary>
    public static CacheEntryOptions Sliding(TimeSpan ttl) => CacheEntryOptions.Sliding(ttl);

    /// <summary>
    /// Creates CacheEntryOptions with absolute expiration using the specified TTL.
    /// </summary>
    public static CacheEntryOptions Absolute(TimeSpan ttl) => CacheEntryOptions.Absolute(ttl);

    /// <summary>
    /// Default options for embedding caching (absolute 1 year).
    /// </summary>
    public static CacheEntryOptions EmbeddingsOptions => CacheEntryOptions.Absolute(Embeddings);

    #endregion
}
