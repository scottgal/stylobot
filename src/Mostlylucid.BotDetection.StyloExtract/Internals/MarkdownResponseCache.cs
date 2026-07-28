using Mostlylucid.Ephemeral.Atoms.SlidingCache;
using Mostlylucid.BotDetection.StyloExtract.Options;

namespace Mostlylucid.BotDetection.StyloExtract.Internals;

/// <summary>
/// Bounded, process-local cache of fully transformed Markdown bodies. The Ephemeral atom
/// supplies LFU eviction (then oldest access), sliding expiry, an absolute lifetime, and
/// concurrent slot creation deduplication. Payload size is bounded separately because the
/// atom's maxSize is an entry count, not a byte budget.
/// </summary>
public sealed class MarkdownResponseCache : IAsyncDisposable
{
    private readonly TransformedContentCacheOptions _options;
    private readonly SlidingCacheAtom<string, Slot> _slots;

    public MarkdownResponseCache(TransformedContentCacheOptions options)
    {
        _options = options;
        if (_options.MaxEntries <= 0 || _options.MaxEntryBytes <= 0 ||
            _options.MaxTotalBytes <= 0 ||
            (long)_options.MaxEntries * _options.MaxEntryBytes > _options.MaxTotalBytes ||
            _options.SlidingExpiration <= TimeSpan.Zero ||
            _options.AbsoluteExpiration < _options.SlidingExpiration)
            throw new ArgumentOutOfRangeException(nameof(options), "Markdown cache bounds are invalid.");

        _slots = new SlidingCacheAtom<string, Slot>(
            (_, _) => Task.FromResult(new Slot()),
            slidingExpiration: _options.SlidingExpiration,
            absoluteExpiration: _options.AbsoluteExpiration,
            maxSize: _options.MaxEntries);
    }

    internal async Task<MarkdownCacheLease> AcquireAsync(string key, CancellationToken ct)
    {
        if (!_options.Enabled) return MarkdownCacheLease.Bypass;
        var slot = await _slots.GetOrComputeAsync(key, ct).ConfigureAwait(false);
        if (slot.TryRead(out var cached)) return new MarkdownCacheLease(key, slot, cached, Fills: false);
        return slot.TryBeginFill()
            ? new MarkdownCacheLease(key, slot, Cached: null, Fills: true)
            : MarkdownCacheLease.Bypass;
    }

    internal void Publish(MarkdownCacheLease lease, byte[] body)
    {
        if (!lease.Fills || body.Length > _options.MaxEntryBytes)
        {
            Discard(lease);
            return;
        }
        lease.Slot!.Publish(body);
    }

    internal void Discard(MarkdownCacheLease lease)
    {
        if (!lease.Fills) return;
        lease.Slot!.Discard();
        _slots.Invalidate(lease.Key!);
    }

    /// <summary>Discard a slot only when no transform callback filled it.</summary>
    internal void AbandonUnfilled(MarkdownCacheLease lease)
    {
        if (!lease.Fills || lease.Slot?.IsFilling != true) return;
        _slots.Invalidate(lease.Key!);
    }

    public ValueTask DisposeAsync() => _slots.DisposeAsync();

    internal sealed class Slot
    {
        private byte[]? _body;
        private int _state; // 0 empty, 1 filling, 2 ready
        public bool TryRead(out byte[]? body)
        {
            body = Volatile.Read(ref _body);
            return Volatile.Read(ref _state) == 2 && body is not null;
        }
        public bool TryBeginFill() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;
        public bool IsFilling => Volatile.Read(ref _state) == 1;
        public void Publish(byte[] body)
        {
            Volatile.Write(ref _body, body);
            Volatile.Write(ref _state, 2);
        }
        public void Discard()
        {
            Volatile.Write(ref _body, null);
            Volatile.Write(ref _state, 0);
        }
    }
}

internal sealed record MarkdownCacheLease(
    string? Key,
    MarkdownResponseCache.Slot? Slot,
    byte[]? Cached,
    bool Fills)
{
    public static MarkdownCacheLease Bypass { get; } = new(null, null, null, false);
}
