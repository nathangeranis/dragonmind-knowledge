using Microsoft.Extensions.Caching.Distributed;

namespace Dragonmind.Knowledge.IntegrationTests;

/// <summary>
/// An <see cref="IDistributedCache"/> pass-through that counts reads, split into hits and misses.
/// <para>
/// <c>Dragonmind.Core.Infrastructure.Caching.DistributedCacheService</c> — the <c>ICacheService</c>
/// implementation <c>AddKnowledgeContext</c> wires up by default — reads via
/// <see cref="Microsoft.Extensions.Caching.Distributed.DistributedCacheExtensions.GetStringAsync"/>,
/// which itself calls <see cref="GetAsync"/> under the hood. So every cache read this test's facade
/// performs, at any layer, is visible here. Counts use <see cref="Interlocked"/> because the facade
/// under test resolves a fresh scope (and can therefore run concurrent reads) per call.
/// </para>
/// </summary>
internal sealed class CountingDistributedCache : IDistributedCache
{
    private readonly IDistributedCache _inner;
    private long _hitCount;
    private long _missCount;

    public CountingDistributedCache(IDistributedCache inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>Number of reads that found a cached value.</summary>
    public long HitCount => Interlocked.Read(ref _hitCount);

    /// <summary>Number of reads that found nothing cached.</summary>
    public long MissCount => Interlocked.Read(ref _missCount);

    /// <inheritdoc />
    public byte[]? Get(string key)
    {
        var value = _inner.Get(key);
        Count(value);
        return value;
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        var value = await _inner.GetAsync(key, token);
        Count(value);
        return value;
    }

    /// <inheritdoc />
    public void Refresh(string key) => _inner.Refresh(key);

    /// <inheritdoc />
    public Task RefreshAsync(string key, CancellationToken token = default) => _inner.RefreshAsync(key, token);

    /// <inheritdoc />
    public void Remove(string key) => _inner.Remove(key);

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken token = default) => _inner.RemoveAsync(key, token);

    /// <inheritdoc />
    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _inner.Set(key, value, options);

    /// <inheritdoc />
    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        => _inner.SetAsync(key, value, options, token);

    private void Count(byte[]? value)
    {
        if (value is null)
        {
            Interlocked.Increment(ref _missCount);
        }
        else
        {
            Interlocked.Increment(ref _hitCount);
        }
    }
}
