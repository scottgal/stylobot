using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Mostlylucid.BotDetection.StyloExtract.Internals;

/// <summary>
///     Per-policy telemetry for the content-cache plane.
///     Counters are exposed via <see cref="System.Diagnostics.Metrics"/> for
///     external scraping (Prometheus, OTEL) and via <see cref="Snapshot"/>
///     for SSR/SignalR dashboard rendering.
/// </summary>
public sealed class ContentCacheTelemetry
{
    private static readonly Meter Meter = new("StyloBot.ContentCache", "1.0");

    private readonly ConcurrentDictionary<string, PolicyCounters> _policies = new();

    /// <summary>Atomic snapshot of one policy's counters.</summary>
    public sealed record PolicySnapshot(
        string PolicyName,
        long Hits,
        long Misses,
        long Bypasses,
        long Evictions,
        long StoreFailures);

    /// <summary>Return a point-in-time snapshot of all registered policy counters.</summary>
    public IReadOnlyList<PolicySnapshot> Snapshot() =>
        _policies.Select(kvp => new PolicySnapshot(
            kvp.Key,
            Interlocked.Read(ref kvp.Value.Hits),
            Interlocked.Read(ref kvp.Value.Misses),
            Interlocked.Read(ref kvp.Value.Bypasses),
            Interlocked.Read(ref kvp.Value.Evictions),
            Interlocked.Read(ref kvp.Value.StoreFailures))).ToList();

    internal void RecordHit(string policyName) => Interlocked.Increment(ref Ensure(policyName).Hits);
    internal void RecordMiss(string policyName) => Interlocked.Increment(ref Ensure(policyName).Misses);
    internal void RecordBypass(string policyName, string reason) => Interlocked.Increment(ref Ensure(policyName).Bypasses);
    internal void RecordEviction(string policyName) => Interlocked.Increment(ref Ensure(policyName).Evictions);
    internal void RecordStoreFailure(string policyName) => Interlocked.Increment(ref Ensure(policyName).StoreFailures);

    private PolicyCounters Ensure(string policyName)
    {
        return _policies.GetOrAdd(policyName, name =>
        {
            var counters = new PolicyCounters();
            // ObservableCounter callbacks run on Meter flush — Prometheus, OTEL, or
            // diagnostics tools read these. Values are monotonic within a process lifetime.
            Meter.CreateObservableCounter($"sb_cache_{name}_hits",      () => Interlocked.Read(ref counters.Hits),      description: $"Cache hits for {name}");
            Meter.CreateObservableCounter($"sb_cache_{name}_misses",    () => Interlocked.Read(ref counters.Misses),    description: $"Cache misses for {name}");
            Meter.CreateObservableCounter($"sb_cache_{name}_bypasses",  () => Interlocked.Read(ref counters.Bypasses),  description: $"Cache bypasses for {name}");
            Meter.CreateObservableCounter($"sb_cache_{name}_evictions", () => Interlocked.Read(ref counters.Evictions), description: $"Cache evictions for {name}");
            Meter.CreateObservableCounter($"sb_cache_{name}_store_failures", () => Interlocked.Read(ref counters.StoreFailures), description: $"Cache store failures for {name}");
            return counters;
        });
    }

    private sealed class PolicyCounters
    {
        public long Hits, Misses, Bypasses, Evictions, StoreFailures;
    }
}
