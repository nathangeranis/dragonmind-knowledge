using System.Security.Cryptography;
using System.Text;

using Dragonmind.Core.Application.Caching;
using Dragonmind.Core.Application.AntiCorruptionLayer.DTOs;
using Dragonmind.Core.Domain.SharedIdentities;

using Microsoft.Extensions.Logging;

namespace Dragonmind.Knowledge.Infrastructure.Caching;

/// <summary>
/// Cache service for knowledge context data (related facts).
/// Note: Knowledge search is NOT cached because it uses semantic search with embeddings
/// which produces different results based on vector similarity, not exact matches.
/// </summary>
public interface IKnowledgeCacheService
{
    /// <summary>
    /// Returns cached related facts for an entity scoped to a scope, loading and caching them via
    /// <paramref name="load"/> on a miss.
    /// </summary>
    /// <remarks>
    /// The scope's generation token is resolved exactly ONCE per call and reused for both the read
    /// and, on a miss, the write below — never re-resolved after <paramref name="load"/> completes.
    /// This closes a generation race that a resolve-read / load / resolve-write split allowed: if
    /// <c>AddKnowledgeFactAsync</c> (via <see cref="InvalidateScopeAsync"/>) rotates the scope's
    /// generation while <paramref name="load"/> is still in flight — which can happen whenever
    /// multiple facts are written concurrently for the same scope — a second generation lookup
    /// after the load would resolve to the NEW generation and cache the pre-write (now stale) facts
    /// under it, where later reads would serve them, silently undoing the invalidation that raced
    /// with the load. Pinning the generation before the load means a rotation mid-flight instead
    /// leaves the write filed under the OLD generation, a key no subsequent read can reach.
    /// The result is cached only when non-empty, matching the previous cache-aside behavior (an
    /// empty result never gets cached, so a name that never resolves to a fact doesn't camp on the
    /// cache for the rest of the entry's TTL).
    /// </remarks>
    /// <param name="scopeId">The scope the facts are scoped to.</param>
    /// <param name="entityName">The entity to look up related facts for.</param>
    /// <param name="maxDepth">The traversal depth the facts were (or will be) computed at.</param>
    /// <param name="load">Invoked on a cache miss to compute the facts; its result is cached under
    /// the generation resolved before it ran.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<KnowledgeFactDto>> GetOrLoadRelatedFactsAsync(
        ScopeId scopeId,
        string entityName,
        int maxDepth,
        Func<CancellationToken, Task<IReadOnlyList<KnowledgeFactDto>>> load,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates every cached related-facts entry for a scope by rotating that scope's
    /// generation token. Called after a new fact has been written for the scope, so every
    /// depth/entity combination previously cached under the old generation stops being reachable
    /// (the composed cache key changes), rather than relying on pattern-based key removal.
    /// </summary>
    /// <remarks>
    /// This is a best-effort rotation, not a guarantee: the production Redis-backed
    /// <c>ICacheService</c> implementation swallows every exception a cache blip raises (it logs and
    /// returns rather than throwing), so the write issued here can silently fail to land. The
    /// implementation therefore reads the generation key before and after writing it, and reports one
    /// of three outcomes at the severity the evidence supports.
    /// <list type="bullet">
    /// <item><description>
    /// <b>Confirmed</b> (<see cref="Microsoft.Extensions.Logging.LogLevel.Debug"/>): the key holds
    /// this call's token, or it demonstrably changed from a pre-rotation token that was read
    /// successfully. It deliberately does not require the key to hold this call's OWN token, because a
    /// caller writing several facts for one scope at once produces overlapping rotations, and one that
    /// loses that race has still achieved invalidation — the old generation is unreachable either way.
    /// </description></item>
    /// <item><description>
    /// <b>Definitely not rotated</b> (<see cref="Microsoft.Extensions.Logging.LogLevel.Error"/>): both
    /// reads succeeded and the key still holds the PRE-rotation token. The marker is dropped (see
    /// below), so this is a failed invalidation worth investigating rather than a live staleness
    /// incident.
    /// </description></item>
    /// <item><description>
    /// <b>Unconfirmed</b> (<see cref="Microsoft.Extensions.Logging.LogLevel.Warning"/>): the
    /// pre-rotation read returned null, which means both "nothing was there" and "the read was
    /// swallowed", so there is no baseline to compare against. Routinely this is just a concurrent
    /// rotation landing first on a key that did not exist yet, but it can also be a dropped write
    /// leaving the old token in place. It stays at Warning rather than
    /// Error because it is no longer a correctness incident and because an alarm firing during
    /// ordinary concurrent writes is one people learn to scroll past, which would cost the Error case
    /// its audience.
    /// </description></item>
    /// </list>
    /// <para>
    /// Both non-confirmed outcomes FAIL CLOSED by removing the generation marker, so a marker this
    /// method cannot vouch for is never used to serve anything. The cost lands on the benign case --
    /// a concurrent rotation whose perfectly good token gets discarded -- which loses cache hits for
    /// that scope until the next write mints a new marker. That trade is deliberate: losing cache hits
    /// is recoverable, and serving a fact the caller has already replaced is not.
    /// </para>
    /// <para>
    /// The removal is itself verified, because the cache swallows a failed removal as readily as a
    /// failed write. If the marker survives the removal attempt AND is still the same token, the
    /// fail-closed step did not happen and stale entries really can stay reachable; that is reported
    /// at <see cref="Microsoft.Extensions.Logging.LogLevel.Error"/> and is the one case here where
    /// the caller's cached facts can go on being wrong until a later write succeeds.
    /// </para>
    /// </remarks>
    Task InvalidateScopeAsync(
        ScopeId scopeId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementation of knowledge caching using ICacheService.
/// Uses 2-hour sliding expiration for knowledge documents.
/// </summary>
public class KnowledgeCacheService : IKnowledgeCacheService
{
    private const string CacheKeyPrefix = "knowledge";
    private const string FactsPrefix = "facts";

    /// <summary>
    /// Cache key version. Bumped from the unversioned, scope-less key scheme so old entries never
    /// collide with the new scope-scoped, generation-qualified ones.
    /// </summary>
    private const string Version = "v2";

    private readonly ICacheService _cacheService;
    private readonly ILogger<KnowledgeCacheService> _logger;
    private readonly CacheEntryOptions _factsCacheOptions;
    private readonly CacheEntryOptions _generationCacheOptions;

    public KnowledgeCacheService(
        ICacheService cacheService,
        ILogger<KnowledgeCacheService> logger)
    {
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _factsCacheOptions = CacheTtl.Sliding(CacheTtl.KnowledgeDocument);

        // SLIDING, and comfortably longer than the facts TTL. Sliding matters: every related-facts
        // read reads this key, which refreshes it, so the marker survives for as long as the scope is
        // actually being used. Under an absolute TTL a long-lived scope that reads constantly but stops
        // WRITING facts would eventually lose its marker, and since the read path deliberately never
        // recreates one, that scope would then read through to the graph on every request forever --
        // caching switched off permanently rather than for a single cold read.
        //
        // This is not load-bearing for correctness. Tokens are random and never reused, so an entry is
        // only ever reachable under the exact token current when it was written; an expired marker
        // costs cache hits, never correctness. It is sized and refreshed to keep that cost rare.
        _generationCacheOptions = CacheTtl.Sliding(CacheTtl.KnowledgeDocument * 2);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<KnowledgeFactDto>> GetOrLoadRelatedFactsAsync(
        ScopeId scopeId,
        string entityName,
        int maxDepth,
        Func<CancellationToken, Task<IReadOnlyList<KnowledgeFactDto>>> load,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopeId, nameof(scopeId));
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        ArgumentNullException.ThrowIfNull(load, nameof(load));

        // Resolved ONCE and reused for both the read below and the write after `load` completes — see
        // the XML doc on the interface method for the generation race this pins closed.
        var generation = await GetGenerationAsync(scopeId, cancellationToken);

        // No generation means no cache, in either direction. ICacheService.GetAsync returns null both
        // for a scope that has never been invalidated and for a read whose exception the cache
        // swallowed, and nothing here can tell those apart. Substituting a fixed placeholder would
        // make a transient read failure resolve to the same key a pre-rotation read had already
        // written under, so a single dropped read could serve exactly the stale facts a rotation had
        // just invalidated. Reading straight through costs one graph traversal and cannot be wrong.
        if (generation is null)
        {
            _logger.LogDebug(
                "No cache generation for scope {ScopeId} - reading related facts through for entity {EntityName}",
                scopeId.Value, entityName);
            return await load(cancellationToken);
        }

        var key = GetRelatedFactsCacheKey(scopeId, generation, entityName, maxDepth);
        var cached = await _cacheService.GetAsync<CachedFactList>(key, cancellationToken);

        if (cached != null)
        {
            _logger.LogDebug(
                "Cache HIT for related facts: scope {ScopeId} entity {EntityName} depth {MaxDepth}",
                scopeId.Value, entityName, maxDepth);
            return cached.Facts;
        }

        _logger.LogDebug(
            "Cache MISS for related facts: scope {ScopeId} entity {EntityName} depth {MaxDepth}",
            scopeId.Value, entityName, maxDepth);

        var facts = await load(cancellationToken);

        if (facts.Count > 0)
        {
            var wrapper = new CachedFactList { Facts = facts.ToList() };
            await _cacheService.SetAsync(key, wrapper, _factsCacheOptions, cancellationToken);
            _logger.LogDebug(
                "Cached {Count} facts for scope {ScopeId} entity: {EntityName}",
                facts.Count, scopeId.Value, entityName);
        }

        return facts;
    }

    /// <inheritdoc />
    public async Task InvalidateScopeAsync(
        ScopeId scopeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopeId, nameof(scopeId));

        var key = GetGenerationCacheKey(scopeId);

        // Capture what is stored BEFORE rotating, so the read-back below can tell "the write never
        // landed" apart from "a concurrent rotation won". This read is best-effort: it can come back
        // null because the key really is absent OR because the read failed, so the check below only
        // relies on it when it returned something. A caller that writes several facts for
        // one scope at once produces overlapping rotations, and those are NOT a failure: any token
        // other than the pre-rotation one makes the old generation unreachable, which is the whole
        // job. Comparing the read-back against THIS call's token would report every one of those
        // crossings as a lost invalidation.
        var previous = await _cacheService.GetAsync<CachedGenerationToken>(key, cancellationToken);

        // Always writes a fresh random token rather than reading and incrementing: a read-then-increment
        // could race with a concurrent rotation and undo it. Storing a new token unconditionally means
        // invalidation takes effect no matter what was there before -- PROVIDED the write itself lands,
        // which is what the read-back below confirms.
        var token = new CachedGenerationToken { Token = Guid.NewGuid().ToString("N") };
        await _cacheService.SetAsync(key, token, _generationCacheOptions, cancellationToken);

        // The production Redis-backed ICacheService implementation swallows every exception a cache
        // blip raises (logs and returns instead of throwing), so a failed write here is otherwise
        // indistinguishable from a successful one. Read the token back before declaring success.
        var confirmed = await _cacheService.GetAsync<CachedGenerationToken>(key, cancellationToken);

        // Three outcomes, each logged at the severity the evidence actually supports.
        //
        // CONFIRMED: the key holds OUR token, or it demonstrably CHANGED from a pre-rotation token we
        // actually read. Either way the old generation is now unreachable, which is the whole job.
        if (confirmed is not null
            && (confirmed.Token == token.Token
                || (previous is not null && confirmed.Token != previous.Token)))
        {
            _logger.LogDebug("Rotated knowledge cache generation for scope {ScopeId}", scopeId.Value);
            return;
        }

        // Everything below is a rotation that could not be confirmed to have landed, so both cases
        // FAIL CLOSED the same way: drop the generation marker. A reader that finds no generation
        // bypasses the cache entirely, so a marker this method cannot vouch for -- which may still be
        // the PRE-rotation one, with its entries reachable -- never gets used to serve anything.
        await _cacheService.RemoveAsync(key, cancellationToken);

        // RemoveAsync swallows its own failures exactly like SetAsync does, so the fail-closed step
        // gets verified rather than assumed. Arguing that a cache too broken to remove a key is
        // probably too broken to serve one is a guess, not a guarantee: a blip can drop this write
        // and let later reads through perfectly well.
        //
        // A token here is only a problem if it is the SAME one that was there before the removal.
        // A different token means another rotation wrote after this removal, which is a fresh
        // generation and leaves nothing stale reachable.
        var afterRemoval = await _cacheService.GetAsync<CachedGenerationToken>(key, cancellationToken);

        // A marker that survived the removal is a problem unless it can be shown to be NEW -- i.e. it
        // differs from a token actually read at some point in this call. Requiring `confirmed` to be
        // non-null was too narrow: when both the pre-read and the read-back are swallowed, `previous`
        // and `confirmed` are both null, and a stale token sitting here would have slipped through as
        // merely "unconfirmed" while readers went on resolving it.
        //
        // With no baseline at all nothing can be proven, so a survivor is treated as stale. That errs
        // towards reporting a rotation that may have been fine, which is the right direction: the
        // alternative is staying quiet about facts the caller has already replaced.
        // A NULL afterRemoval is not proof the marker went away either -- RemoveAsync and GetAsync
        // both swallow their failures, so "the key is gone" and "the read failed" arrive identically.
        // If the removal was dropped AND this verification read was dropped, the old marker is still
        // sitting there and later reads will resolve it.
        //
        // That residual cannot be closed from inside this method. Every branch here already reads
        // back rather than assuming, and the one thing still missing is a cache operation that can
        // report its own failure; ICacheService returns null for a miss, a deserialisation failure
        // and a transport failure alike. Closing it properly means giving that interface an
        // observable result, which is a Foundation change touching every caching facade in the
        // solution -- deliberately not bundled into a Knowledge bugfix.
        //
        // What bounds it meanwhile: three consecutive swallowed cache operations have to coincide,
        // and the entries at risk expire on their own sliding TTL. The Error path below fires
        // whenever a survivor IS observed, so the case that goes unreported is specifically the one
        // where the cache is failing badly enough that reads are failing too -- in which case
        // GetGenerationAsync returns null and readers bypass the cache anyway.
        var provablyNew = afterRemoval is not null
            && ((confirmed is not null && afterRemoval.Token != confirmed.Token)
                || (previous is not null && afterRemoval.Token != previous.Token));

        // A token that IS provably new means another rotation wrote after this removal. The old
        // generation is unreachable either way, which is the entire job, so this is a success and not
        // a failure -- even when this call's own write was the one that went missing. Returning here
        // also keeps the "did not take effect" branch below honest: it can now only be reached with
        // the marker actually gone, which is what its message says happened.
        if (provablyNew)
        {
            _logger.LogDebug(
                "Knowledge cache generation for scope {ScopeId} was rotated by a concurrent write",
                scopeId.Value);
            return;
        }

        if (afterRemoval is not null)
        {
            _logger.LogError(
                "Knowledge cache generation rotation for scope {ScopeId} could not be completed - the rotation " +
                "was not confirmed AND the generation marker could not be dropped, so cached related " +
                "facts for this scope can stay reachable and stale until a later write succeeds",
                scopeId.Value);
            return;
        }

        // DEFINITELY NOT ROTATED: the key still holds the very token it held before, read successfully
        // on both sides. That is a real failed invalidation and worth an Error, even though the
        // removal above means it is no longer a staleness incident on its own.
        if (confirmed is not null && previous is not null && confirmed.Token == previous.Token)
        {
            _logger.LogError(
                "Knowledge cache generation rotation for scope {ScopeId} did not take effect - the generation " +
                "key still holds its pre-rotation token; dropped the marker so reads bypass the cache " +
                "until the next write",
                scopeId.Value);
            return;
        }

        // UNCONFIRMED: the pre-rotation read returned null, which means both "no generation yet" and
        // "the cache swallowed the read", so there is no baseline to compare the read-back against.
        // The ordinary cause is another rotation landing between this write and this read-back --
        // several fact writes run concurrently for one scope, and the first of them race on a key that
        // does not exist yet, so this is routine rather than exceptional. A genuine failure needs a
        // lost read AND a lost write together, and even then a reader that cannot resolve the
        // generation bypasses the cache rather than serving anything stale.
        //
        // Warning rather than Error deliberately: an alarm that fires during normal concurrent writes
        // is one people learn to scroll past, and that would cost the Error above the audience it
        // needs. This says what is and is not known instead of asserting an outcome.
        _logger.LogWarning(
            "Could not confirm the knowledge cache generation rotation for scope {ScopeId} - the pre-rotation " +
            "value was unreadable, so a concurrent rotation and a dropped write are indistinguishable " +
            "here; dropped the generation marker so reads bypass the cache until the next write",
            scopeId.Value);
    }

    #region Cache Key Generation

    /// <summary>
    /// Reads the scope's current generation token, or null when there is none to be had.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This read path deliberately never WRITES the generation key. Minting and storing a token on a
    /// miss via <c>ICacheService.GetOrSetAsync</c> looks safe but is not: that method's stampede guard
    /// is a per-process lock keyed on the cache key, so it serializes <c>GetOrSetAsync</c> callers
    /// against each other and not at all against <see cref="InvalidateScopeAsync"/>'s <c>SetAsync</c>.
    /// A reader could find the key missing, pass the double-check, and then write its own token over a
    /// rotation that landed in between — after that rotation had already read its token back and
    /// declared success — leaving the reader's token reachable and its pre-write facts cached under
    /// it. Reading without writing removes that interleaving rather than trying to detect it, and takes
    /// a cache write off every related-facts read as a side benefit.
    /// </para>
    /// <para>
    /// Null means "no usable generation" and deliberately does NOT distinguish a scope that has never
    /// been invalidated from a read the cache swallowed an exception on, because the caller treats both
    /// the same way: bypass the cache. A token is a fresh random value on every rotation and is never
    /// reused, so an entry is only ever reachable under the exact token that was current when it was
    /// written. Nothing can resurrect an entry a rotation left behind.
    /// </para>
    /// </remarks>
    private async Task<string?> GetGenerationAsync(ScopeId scopeId, CancellationToken cancellationToken)
    {
        var generation = await _cacheService.GetAsync<CachedGenerationToken>(
            GetGenerationCacheKey(scopeId),
            cancellationToken);

        return string.IsNullOrEmpty(generation?.Token) ? null : generation.Token;
    }

    private static string GetGenerationCacheKey(ScopeId scopeId)
    {
        return $"{CacheKeyPrefix}:{FactsPrefix}:{Version}:{scopeId.Value:N}:generation";
    }

    private static string GetRelatedFactsCacheKey(ScopeId scopeId, string generation, string entityName, int maxDepth)
    {
        return $"{CacheKeyPrefix}:{FactsPrefix}:{Version}:{scopeId.Value:N}:{generation}:{HashEntityName(entityName)}:depth:{maxDepth}";
    }

    /// <summary>
    /// Derives the entity segment of the cache key from the exact entity name.
    /// </summary>
    /// <remarks>
    /// The name is hashed rather than normalized because the graph matches it EXACTLY and
    /// case-sensitively (<c>WHERE a.name = '...'</c> in <c>ApacheAgeKnowledgeGraphRepository</c>), so
    /// the key has to distinguish every name the graph distinguishes. Lowercasing the name and
    /// replacing spaces with underscores — the obvious "just normalize it" move — collapses
    /// "Checkout Service", "checkout service" and "checkout_service" onto a single entry: three
    /// entities the graph treats as distinct sharing one cache slot, so whichever was looked up
    /// first has its facts served for the other two. Hashing also keeps the ':' key delimiter out
    /// of a segment built from caller-supplied text.
    /// </remarks>
    private static string HashEntityName(string entityName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(entityName));
        return Convert.ToHexStringLower(hash);
    }

    #endregion

    #region Cache Wrapper Classes

    /// <summary>
    /// Wrapper class for caching fact lists.
    /// Required because ICacheService.GetAsync expects a class type.
    /// </summary>
    internal sealed class CachedFactList
    {
        public List<KnowledgeFactDto> Facts { get; init; } = new();
    }

    /// <summary>
    /// Wrapper class for the per-scope generation token.
    /// Required because ICacheService.GetAsync/SetAsync expect a class type — a bare string cannot
    /// be used directly.
    /// </summary>
    internal sealed class CachedGenerationToken
    {
        public string Token { get; init; } = string.Empty;
    }

    #endregion
}
