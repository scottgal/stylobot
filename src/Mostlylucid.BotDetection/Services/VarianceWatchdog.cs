using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Policies;

namespace Mostlylucid.BotDetection.Services;

public readonly record struct WatchdogResult(bool Tripped, string? Reason);

/// <summary>
///     Cheap per-signature checks that guard the Skip path: tripped = invalidate
///     cached verdict and run the full pipeline. Bounded by <see cref="MaxFingerprints"/>
///     with LRU-by-last-seen eviction so the dictionary cannot grow without limit.
/// </summary>
public sealed class VarianceWatchdog
{
    private const int MaxFingerprints = 10_000;
    private const int PruneTrigger = MaxFingerprints + (MaxFingerprints / 5);
    private const int ObservationCap = 600;

    private static readonly WatchdogResult NotTripped = new(false, null);

    private readonly ILogger<VarianceWatchdog> _logger;
    private readonly ConcurrentDictionary<string, FingerprintHistory> _history = new();
    private int _pruneInFlight;

    private sealed class FingerprintHistory
    {
        public string? LastIp24;
        public DateTime LastIp24SeenUtc;
        public DateTime LastSeenUtc;
        public readonly ConcurrentQueue<DateTime> RecentObservations = new();
    }

    public VarianceWatchdog(ILogger<VarianceWatchdog> logger) => _logger = logger;

    public void RecordObservation(string signature, string clientIp, string path)
    {
        var hist = _history.GetOrAdd(signature, _ => new FingerprintHistory());
        var slash24 = Slash24(clientIp);
        var now = DateTime.UtcNow;
        if (slash24 is not null)
        {
            hist.LastIp24 = slash24;
            hist.LastIp24SeenUtc = now;
        }
        hist.LastSeenUtc = now;
        hist.RecentObservations.Enqueue(now);
        TrimObservations(hist, now);

        if (_history.Count >= PruneTrigger)
            TryPruneStale(now);
    }

    public WatchdogResult Check(HttpContext ctx, string signature, SignatureVerdict cached, VarianceWatchdogOptions options)
    {
        if (!options.Enabled || !_history.TryGetValue(signature, out var hist))
            return NotTripped;

        var now = DateTime.UtcNow;

        if (options.IpRotationWindowSeconds > 0 && hist.LastIp24 is { } prevIp)
        {
            var currentIp = Slash24(ctx.Connection.RemoteIpAddress?.ToString());
            if (currentIp is not null
                && !string.Equals(currentIp, prevIp, StringComparison.Ordinal)
                && (now - hist.LastIp24SeenUtc).TotalSeconds <= options.IpRotationWindowSeconds)
            {
                return new WatchdogResult(true, $"ip-rotation:{prevIp}->{currentIp}");
            }
        }

        if (options.RateSpikeMultiplier > 0)
        {
            var (current, baseline) = ComputeRates(hist, now);
            if (baseline > 0 && current >= baseline * options.RateSpikeMultiplier)
                return new WatchdogResult(true, $"rate-spike:{current:F1}vs{baseline:F1}");
        }

        return NotTripped;
    }

    private static string? Slash24(string? ip)
    {
        if (string.IsNullOrEmpty(ip)) return null;
        if (!IPAddress.TryParse(ip, out var addr)) return null;
        if (addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return ip;
        Span<byte> bytes = stackalloc byte[4];
        return addr.TryWriteBytes(bytes, out _)
            ? string.Create(null, stackalloc char[16], $"{bytes[0]}.{bytes[1]}.{bytes[2]}")
            : null;
    }

    private static (int current, double baseline) ComputeRates(FingerprintHistory hist, DateTime now)
    {
        var oneMinuteAgo = now - TimeSpan.FromMinutes(1);
        var fiveMinutesAgo = now - TimeSpan.FromMinutes(5);
        var currentCount = 0;
        var baselineCount = 0;
        foreach (var t in hist.RecentObservations)
        {
            if (t >= oneMinuteAgo) currentCount++;
            if (t >= fiveMinutesAgo) baselineCount++;
        }
        return (currentCount, baselineCount / 5.0);
    }

    private static void TrimObservations(FingerprintHistory hist, DateTime now)
    {
        var cutoff = now - TimeSpan.FromMinutes(5);
        while (hist.RecentObservations.TryPeek(out var t) && t < cutoff)
            hist.RecentObservations.TryDequeue(out _);

        // Hard cap on per-fingerprint queue size in case a single signature floods.
        // Older entries fall off first so the rate computation stays useful.
        var overflow = hist.RecentObservations.Count - ObservationCap;
        while (overflow-- > 0 && hist.RecentObservations.TryDequeue(out _)) { }
    }

    /// <summary>
    ///     Coordinator-style LRU prune: drop the oldest fingerprints when the
    ///     dictionary exceeds <see cref="PruneTrigger"/>. Single-flight via interlocked
    ///     guard so a burst of concurrent observations does not stampede the prune.
    /// </summary>
    private void TryPruneStale(DateTime now)
    {
        if (Interlocked.CompareExchange(ref _pruneInFlight, 1, 0) != 0) return;
        try
        {
            var targetEvictions = _history.Count - MaxFingerprints;
            if (targetEvictions <= 0) return;

            DateTime cutoff = default;
            var cold = 0;
            foreach (var kv in _history)
            {
                if ((now - kv.Value.LastSeenUtc) > TimeSpan.FromMinutes(30))
                {
                    _history.TryRemove(kv.Key, out _);
                    if (++cold >= targetEvictions) return;
                    if (kv.Value.LastSeenUtc > cutoff) cutoff = kv.Value.LastSeenUtc;
                }
            }

            // If TTL-based pruning didn't free enough slots, drop the oldest-by-LastSeen
            // until we hit the target.
            if (cold < targetEvictions)
            {
                foreach (var kv in _history.OrderBy(static x => x.Value.LastSeenUtc))
                {
                    _history.TryRemove(kv.Key, out _);
                    if (++cold >= targetEvictions) return;
                }
            }
        }
        finally
        {
            Volatile.Write(ref _pruneInFlight, 0);
        }
    }
}
