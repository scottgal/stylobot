using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.ApiHolodeck.Models;

namespace Mostlylucid.BotDetection.ApiHolodeck.Services;

/// <summary>
///     Manages concurrent holodeck engagements. One slot per fingerprint,
///     global capacity cap. When a slot is busy or capacity is full,
///     the request falls through to normal 403 blocking.
/// </summary>
public sealed class HolodeckCoordinator
{
    private readonly ConcurrentDictionary<string, byte> _activeSlots = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxConcurrent;
    private readonly int _maxPerFingerprint;
    private int _activeCount;

    public HolodeckCoordinator(IOptions<HolodeckOptions> options)
    {
        _maxConcurrent = options.Value.MaxConcurrentEngagements;
        _maxPerFingerprint = options.Value.MaxEngagementsPerFingerprint;
    }

    public int ActiveEngagements => _activeCount;
    public int Capacity => _maxConcurrent;

    public bool TryEngage(string fingerprint, out IDisposable? slot)
    {
        slot = null;

        if (Interlocked.CompareExchange(ref _activeCount, 0, 0) >= _maxConcurrent)
            return false;

        if (!_activeSlots.TryAdd(fingerprint, 0))
            return false;

        if (Interlocked.Increment(ref _activeCount) > _maxConcurrent)
        {
            _activeSlots.TryRemove(fingerprint, out _);
            Interlocked.Decrement(ref _activeCount);
            return false;
        }

        slot = new EngagementSlot(this, fingerprint);
        return true;
    }

    private void Release(string fingerprint)
    {
        _activeSlots.TryRemove(fingerprint, out _);
        Interlocked.Decrement(ref _activeCount);
    }

    private sealed class EngagementSlot : IDisposable
    {
        private readonly HolodeckCoordinator _coordinator;
        private readonly string _fingerprint;
        private int _disposed;

        public EngagementSlot(HolodeckCoordinator coordinator, string fingerprint)
        {
            _coordinator = coordinator;
            _fingerprint = fingerprint;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _coordinator.Release(_fingerprint);
        }
    }
}
