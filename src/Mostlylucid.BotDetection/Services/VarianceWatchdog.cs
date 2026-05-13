using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Policies;

namespace Mostlylucid.BotDetection.Services;

public sealed record WatchdogResult(bool Tripped, string? Reason);

/// <summary>
///     Lightweight per-signature watchdog that guards the Skip path of the
///     SignatureVerdictGate. The middleware calls <see cref="CheckAsync"/> on every
///     Skip candidate; a Tripped result downgrades the request to a full-pipeline
///     run. The middleware ALSO calls <see cref="RecordObservationAsync"/> on every
///     request (Skip, Bias, or Miss) so the watchdog's state stays current.
///
///     The watchdog does NOT score the request and is NOT a detector. Its only job
///     is to detect that a known fingerprint is doing something unusual enough that
///     the cached verdict should be invalidated and the full pipeline rerun.
/// </summary>
public sealed class VarianceWatchdog
{
    private readonly ILogger<VarianceWatchdog> _logger;
    private readonly ConcurrentDictionary<string, FingerprintHistory> _history = new();

    private sealed class FingerprintHistory
    {
        public string? LastIp24;
        public DateTime LastIp24SeenUtc;
        public readonly ConcurrentQueue<DateTime> RecentObservations = new();
    }

    public VarianceWatchdog(ILogger<VarianceWatchdog> logger) => _logger = logger;

    public Task RecordObservationAsync(string signature, string clientIp, string path, CancellationToken ct = default)
    {
        var hist = _history.GetOrAdd(signature, _ => new FingerprintHistory());
        var slash24 = Slash24(clientIp);
        if (slash24 is not null)
        {
            hist.LastIp24 = slash24;
            hist.LastIp24SeenUtc = DateTime.UtcNow;
        }
        hist.RecentObservations.Enqueue(DateTime.UtcNow);
        TrimObservationsOlderThan(hist, TimeSpan.FromMinutes(5));
        return Task.CompletedTask;
    }

    public Task<WatchdogResult> CheckAsync(
        HttpContext ctx,
        string signature,
        SignatureVerdict cached,
        VarianceWatchdogOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
            return Task.FromResult(new WatchdogResult(false, null));

        if (!_history.TryGetValue(signature, out var hist))
            return Task.FromResult(new WatchdogResult(false, null));

        // IP rotation check
        if (options.IpRotationWindowSeconds > 0 && hist.LastIp24 is { } prevIp)
        {
            var currentIp = Slash24(ctx.Connection.RemoteIpAddress?.ToString());
            if (currentIp is not null
                && !string.Equals(currentIp, prevIp, StringComparison.Ordinal)
                && (DateTime.UtcNow - hist.LastIp24SeenUtc).TotalSeconds <= options.IpRotationWindowSeconds)
            {
                return Task.FromResult(new WatchdogResult(true,
                    $"ip-rotation:{prevIp}->{currentIp}"));
            }
        }

        // Rate spike check: current 1-minute rate vs 5-minute baseline
        if (options.RateSpikeMultiplier > 0)
        {
            var (current, baseline) = ComputeRates(hist);
            if (baseline > 0 && current >= baseline * options.RateSpikeMultiplier)
            {
                return Task.FromResult(new WatchdogResult(true,
                    $"rate-spike:{current:F1}vs{baseline:F1}"));
            }
        }

        // CheckPathCentroid: follow-up. Requires CentroidSequenceStore integration.

        return Task.FromResult(new WatchdogResult(false, null));
    }

    private static string? Slash24(string? ip)
    {
        if (string.IsNullOrEmpty(ip)) return null;
        if (!IPAddress.TryParse(ip, out var addr)) return null;
        if (addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return ip; // IPv6: full address as key (a /48-style aggregation could come later)
        var bytes = addr.GetAddressBytes();
        return $"{bytes[0]}.{bytes[1]}.{bytes[2]}";
    }

    private static (double current, double baseline) ComputeRates(FingerprintHistory hist)
    {
        var now = DateTime.UtcNow;
        var oneMinuteAgo = now - TimeSpan.FromMinutes(1);
        var fiveMinutesAgo = now - TimeSpan.FromMinutes(5);
        var currentCount = 0;
        var baselineCount = 0;
        foreach (var t in hist.RecentObservations)
        {
            if (t >= oneMinuteAgo) currentCount++;
            if (t >= fiveMinutesAgo) baselineCount++;
        }
        return (currentCount, baselineCount / 5.0); // per-minute
    }

    private static void TrimObservationsOlderThan(FingerprintHistory hist, TimeSpan window)
    {
        var cutoff = DateTime.UtcNow - window;
        while (hist.RecentObservations.TryPeek(out var t) && t < cutoff)
            hist.RecentObservations.TryDequeue(out _);
    }
}
