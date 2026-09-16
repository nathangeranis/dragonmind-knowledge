namespace Dragonmind.Knowledge.Infrastructure.DI;

/// <summary>
/// Host-configurable options for the Knowledge bounded context, bound via the
/// <c>configure</c> delegate passed to <c>AddKnowledgeContext</c>.
/// </summary>
public sealed class KnowledgeOptions
{
    /// <summary>
    /// The name of the embedding model in use. This is part of the embedding cache key
    /// (see <see cref="Dragonmind.Knowledge.Infrastructure.Embeddings.CachingEmbeddingService"/>
    /// and <see cref="Dragonmind.Knowledge.Infrastructure.Caching.IEmbeddingCacheService"/>), so a
    /// model change yields distinct cache entries rather than silently reusing vectors generated
    /// by a different model. Defaults to a stable placeholder when the host does not configure one,
    /// since the cache key requires a non-empty model name.
    /// </summary>
    public string EmbeddingModelName { get; set; } = "default-embedding";
}
