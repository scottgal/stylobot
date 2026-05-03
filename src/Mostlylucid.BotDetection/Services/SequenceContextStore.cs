using System.Collections.Concurrent;
using System.Collections.Immutable;
using Mostlylucid.BotDetection.Analysis;

namespace Mostlylucid.BotDetection.Services;

/// <summary>Classification of a sequence context's centroid — human, bot, or unknown.</summary>
public enum CentroidType { Unknown = 0, Human = 1, Bot = 2 }

/// <summary>
///     Immutable snapshot of a fingerprint's current position in its content sequence.
///     Updated on each request by ContentSequenceContributor.
/// </summary>
public sealed record SequenceContext
{
    public required string ChainId { get; init; }
    public required string Signature { get; init; }
    public string CentroidId { get; init; } = string.Empty;
    public CentroidType CentroidType { get; init; } = CentroidType.Unknown;
    public int Position { get; init; }
    public RequestState[] ExpectedChain { get; init; } = Array.Empty<RequestState>();
    public double[] TypicalGapsMs { get; init; } = Array.Empty<double>();
    public double[] GapToleranceMs { get; init; } = Array.Empty<double>();
    public ImmutableHashSet<RequestState> ObservedStateSet { get; init; } = ImmutableHashSet<RequestState>.Empty;
    public DateTimeOffset WindowStartTime { get; init; } = DateTimeOffset.UtcNow;
    public int RequestCountInWindow { get; init; }
    public DateTimeOffset LastRequest { get; init; } = DateTimeOffset.UtcNow;
    public bool HasDiverged { get; init; }
    public int DivergenceCount { get; init; }
    public bool CacheWarm { get; init; }
    public string ContentPath { get; init; } = string.Empty;
}

/// <summary>
///     Transient per-fingerprint sequence state. ConcurrentDictionary backed — no SQLite.
///     Loss on restart is acceptable: fingerprints just get a fresh context.
///     TTL sweep runs every 5 minutes, evicts entries older than the session gap.
/// </summary>
public sealed class SequenceContextStore : IDisposable
{
    private readonly ConcurrentDictionary<string, SequenceContext> _contexts = new();
    private readonly Timer _sweepTimer;
    private static readonly TimeSpan DefaultSessionGap = TimeSpan.FromMinutes(30);

    public SequenceContextStore()
    {
        _sweepTimer = new Timer(
            _ => EvictExpired(DefaultSessionGap),
            null,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(5));
    }

    /// <summary>
    ///     Get the existing context, or create a fresh one at position 0.
    ///     If the existing context is older than <paramref name="sessionGapMinutes"/>, replace it (session boundary).
    /// </summary>
    public SequenceContext GetOrCreate(string signature, int sessionGapMinutes = 30)
    {
        if (_contexts.TryGetValue(signature, out var existing))
        {
            var gap = DateTimeOffset.UtcNow - existing.LastRequest;
            if (gap.TotalMinutes < sessionGapMinutes)
                return existing;

            var renewed = CreateFresh(signature);
            _contexts[signature] = renewed;
            return renewed;
        }

        var fresh = CreateFresh(signature);
        _contexts[signature] = fresh;
        return fresh;
    }

    /// <summary>Atomically store an updated context.</summary>
    public void Update(string signature, SequenceContext updated)
        => _contexts[signature] = updated;

    /// <summary>Retrieve without creating. Returns null if not found.</summary>
    public SequenceContext? TryGet(string signature)
        => _contexts.TryGetValue(signature, out var ctx) ? ctx : null;

    /// <summary>Remove all entries older than <paramref name="sessionGap"/>.</summary>
    public void EvictExpired(TimeSpan sessionGap)
    {
        var cutoff = DateTimeOffset.UtcNow - sessionGap;
        foreach (var key in _contexts.Keys)
        {
            if (_contexts.TryGetValue(key, out var ctx) && ctx.LastRequest < cutoff)
                _contexts.TryRemove(key, out _);
        }
    }

    public int Count => _contexts.Count;

    public void Dispose() => _sweepTimer.Dispose();

    private static SequenceContext CreateFresh(string signature) => new()
    {
        ChainId = Guid.NewGuid().ToString("N"),
        Signature = signature
    };
}
