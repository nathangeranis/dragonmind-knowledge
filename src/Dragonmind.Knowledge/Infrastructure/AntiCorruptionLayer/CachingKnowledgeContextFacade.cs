using Dragonmind.Core.Application.AntiCorruptionLayer;
using Dragonmind.Core.Domain.SharedIdentities;
using Dragonmind.Core.Application.AntiCorruptionLayer.DTOs;
using Dragonmind.Knowledge.Infrastructure.Caching;

using Microsoft.Extensions.Logging;

namespace Dragonmind.Knowledge.Infrastructure.AntiCorruptionLayer;

/// <summary>
/// Caching decorator for IKnowledgeContextFacade.
/// Implements cache-aside pattern: reads check cache first, cache on miss.
///
/// Note: SearchKnowledgeAsync is NOT cached because semantic search results
/// vary based on embedding similarity and should always be computed fresh.
/// Only GetRelatedFactsAsync benefits from caching.
/// </summary>
public sealed class CachingKnowledgeContextFacade : IKnowledgeContextFacade
{
    private readonly IKnowledgeContextFacade _inner;
    private readonly IKnowledgeCacheService _cacheService;
    private readonly ILogger<CachingKnowledgeContextFacade> _logger;

    public CachingKnowledgeContextFacade(
        IKnowledgeContextFacade inner,
        IKnowledgeCacheService cacheService,
        ILogger<CachingKnowledgeContextFacade> logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    /// <remarks>
    /// NOT cached - semantic search uses vector similarity which produces
    /// different results based on embedding proximity, not exact query matches.
    /// </remarks>
    public Task<IReadOnlyList<KnowledgeSnippetDto>> SearchKnowledgeAsync(
        string query,
        ScopeId? scopeId,
        int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        // Semantic search is NOT cached - results vary by embedding similarity
        _logger.LogDebug("Knowledge search bypassing cache (semantic search): {Query}", query);
        return _inner.SearchKnowledgeAsync(query, scopeId, maxResults, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Write operation - invalidates cache after storing.
    /// </remarks>
    public async Task<DocumentId> StoreKnowledgeAsync(
        string content,
        string source,
        ScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        // Write operation - just delegate to inner
        // Cache invalidation happens on related facts when entities are updated
        var result = await _inner.StoreKnowledgeAsync(content, source, scopeId, cancellationToken);
        _logger.LogDebug("Stored knowledge document {DocumentId}", result.Value);
        return result;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Delegates the whole cache-aside dance to <see cref="IKnowledgeCacheService.GetOrLoadRelatedFactsAsync"/>
    /// rather than doing a separate cache read here followed by a separate cache write below, on a
    /// miss, once <c>_inner</c> returns. That split used to re-resolve the scope's cache generation
    /// twice — once for the read, once for the write — with the graph load from <c>_inner</c> running
    /// in between. If <see cref="AddKnowledgeFactAsync"/> rotated the generation while that load was
    /// still in flight, the write would land under the NEW generation and serve the pre-write
    /// (already-stale) facts back out, silently undoing the invalidation that raced with it. Passing
    /// the load in as a callback lets the cache service resolve the generation exactly once and reuse
    /// it for both the read and the write, closing that race.
    /// </remarks>
    public Task<IReadOnlyList<KnowledgeFactDto>> GetRelatedFactsAsync(
        string entityName,
        ScopeId scopeId,
        int maxDepth = 2,
        CancellationToken cancellationToken = default)
    {
        return _cacheService.GetOrLoadRelatedFactsAsync(
            scopeId,
            entityName,
            maxDepth,
            ct => _inner.GetRelatedFactsAsync(entityName, scopeId, maxDepth, ct),
            cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Write operation. The write happens first; only once it has completed AND returned
    /// <c>true</c> is the scope's related-facts cache invalidated (via a fresh generation token —
    /// see <see cref="IKnowledgeCacheService.InvalidateScopeAsync"/>), using
    /// <see cref="CancellationToken.None"/> rather than the caller's token. A caller that cancels
    /// after the write already committed must not be allowed to skip invalidation and leave stale
    /// facts cached — and a cancelled-token removal on a distributed cache is easy to swallow and
    /// log as success without evicting anything, so honouring the caller's token here would fail
    /// silently.
    /// </para>
    /// <para>
    /// A write that is REJECTED (unknown predicate) invalidates nothing, because nothing was
    /// written. A write that THROWS does invalidate, because it is not known whether it landed: the
    /// repository forwards the caller's token to an autocommit graph write, so the mutation can
    /// commit and the cancellation only be observed as the await completes. Treating "don't know" as
    /// "did" costs cache hits; treating it as "did not" would leave the pre-write generation
    /// reachable with the new fact already in the graph.
    /// </para>
    /// </remarks>
    public async Task<bool> AddKnowledgeFactAsync(
        string subject,
        string predicate,
        string @object,
        ScopeId scopeId,
        string subjectType = "Entity",
        string objectType = "Entity",
        CancellationToken cancellationToken = default)
    {
        var written = false;
        var completed = false;

        try
        {
            written = await _inner.AddKnowledgeFactAsync(subject, predicate, @object, scopeId, subjectType, objectType, cancellationToken);
            completed = true;
        }
        finally
        {
            // Invalidate unless it is KNOWN that nothing was written. An exception leaves `written`
            // false while the fact may well be in the graph -- a cancellation observed after the
            // autocommit write is exactly that shape -- so "did not complete" counts as "may have
            // written".
            if (!completed || written)
            {
                await _cacheService.InvalidateScopeAsync(scopeId, CancellationToken.None);
            }
        }

        if (written)
        {
            // "Requested", not "invalidated": the rotation is best-effort and reports a failure by
            // logging at Error and returning, so claiming success here would put a cheerful Debug
            // line immediately after that Error and make a stale-cache incident read like a normal
            // write.
            _logger.LogDebug(
                "Added knowledge fact and requested scope cache invalidation for scope {ScopeId}: {Subject}, {Object}",
                scopeId.Value, subject, @object);
        }

        return written;
    }
}
