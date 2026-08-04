using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Mostlylucid.BotDetection.StyloExtract.ContentCache;

/// <summary>Per-policy counter snapshot for the dashboard read model.</summary>
public sealed record ContentCachePolicyCounters(
    string Policy,
    long Hits,
    long Misses,
    long Bypasses,
    long Evictions);

/// <summary>
///     Content-cache observability: per-policy hit / miss / bypass / eviction counters.
///     Counters are exported via <see cref="System.Diagnostics.Metrics"/> (OTel / Prometheus via the
///     Observability pack) and exposed as a lock-free snapshot for the dashboard policy tab
///     (<see cref="Snapshot"/>), which the dashboard agent wires into <c>IPolicyStateProvider</c>.
/// </summary>
public interface IContentCacheTelemetry
{
    /// <summary>A cacheable request was served from the cache.</summary>
    void Hit(string policy);

    /// <summary>A cacheable request found no entry and began a fill.</summary>
    void Miss(string policy);

    /// <summary>A request or response was deliberately not cached (request/response rule, non-GET, not eligible).</summary>
    void Bypass(string policy);

    /// <summary>A claimed fill never produced a servable entry (abandoned fill, transform failure, oversize discard).</summary>
    void Eviction(string policy);

    /// <summary>Lock-free per-policy snapshot for the dashboard.</summary>
    IReadOnlyList<ContentCachePolicyCounters> Snapshot();
}

/// <inheritdoc cref="IContentCacheTelemetry"/>
public sealed class ContentCacheTelemetry : IContentCacheTelemetry, IDisposable
{
    private readonly Meter _meter;
    private readonly Counter<long> _hits;
    private readonly Counter<long> _misses;
    private readonly Counter<long> _bypasses;
    private readonly Counter<long> _evictions;
    private readonly ConcurrentDictionary<string, PolicyCounter> _counters = new(StringComparer.Ordinal);

    public ContentCacheTelemetry()
    {
        _meter = new Meter("stylobot.content-cache");
        _hits = _meter.CreateCounter<long>("content_cache.hits", "responses", "Cache hits served per policy");
        _misses = _meter.CreateCounter<long>("content_cache.misses", "requests", "Cache misses per policy");
        _bypasses = _meter.CreateCounter<long>("content_cache.bypasses", "requests", "Not-cacheable traffic per policy");
        _evictions = _meter.CreateCounter<long>("content_cache.evictions", "entries", "Cache-slot releases without a served entry per policy");
    }

    public void Hit(string policy) { Increment(policy, static c => Interlocked.Increment(ref c.Hits)); _hits.Add(1, Tag(policy)); }
    public void Miss(string policy) { Increment(policy, static c => Interlocked.Increment(ref c.Misses)); _misses.Add(1, Tag(policy)); }
    public void Bypass(string policy) { Increment(policy, static c => Interlocked.Increment(ref c.Bypasses)); _bypasses.Add(1, Tag(policy)); }
    public void Eviction(string policy) { Increment(policy, static c => Interlocked.Increment(ref c.Evictions)); _evictions.Add(1, Tag(policy)); }

    // Compatibility aliases used by the legacy content-cache / extract-markdown policy classes
    // (kept compiling until their references are migrated to the two visible policies).
    public void RecordHit(string policy) => Hit(policy);
    public void RecordMiss(string policy) => Miss(policy);
    public void RecordBypass(string policy, string? reason = null) => Bypass(policy);
    public void RecordStoreFailure(string policy) => Eviction(policy);

    public IReadOnlyList<ContentCachePolicyCounters> Snapshot()
        => _counters.Select(pair => new ContentCachePolicyCounters(
                pair.Key,
                Volatile.Read(ref pair.Value.Hits),
                Volatile.Read(ref pair.Value.Misses),
                Volatile.Read(ref pair.Value.Bypasses),
                Volatile.Read(ref pair.Value.Evictions)))
            .ToList();

    public void Dispose() => _meter.Dispose();

    private void Increment(string policy, Action<PolicyCounter> mutate)
        => mutate(_counters.GetOrAdd(policy, static _ => new PolicyCounter()));

    private static TagList Tag(string policy) => new() { { "policy", policy } };

    /// <summary>Mutable long fields so the hot path uses Interlocked, never locks.</summary>
    private sealed class PolicyCounter
    {
        public long Hits;
        public long Misses;
        public long Bypasses;
        public long Evictions;
    }
}
