using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.RateLimit;

/// <summary>
///     Rolling-window upstream health tracker. Maintains exponentially-
///     weighted moving averages of:
///     <list type="bullet">
///         <item><c>response.error_rate_5xx</c> -- fraction of recent requests that returned 5xx.</item>
///         <item><c>response.rate_429</c> -- fraction returning 429.</item>
///         <item><c>response.latency_ema</c> -- EMA of response latency in milliseconds.</item>
///     </list>
///     Adapted from PR #16's reaction-pack work; the pack framework around
///     it was over-engineered for our needs, but the EMA primitive itself
///     is a clean fit for adaptive rate-limit scaling.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="RecordResponse"/> is called once per upstream
///         response (status code + latency + path). Inside-process state
///         only -- per the no-in-memory-store rule's "transient
///         per-request state" carve-out (a process restart loses ~60s of
///         smoothing, then the EMAs re-converge).
///     </para>
///     <para>
///         The background <see cref="Decay"/> timer prevents stale buckets
///         from poisoning the average forever: every 5s, all values decay
///         toward zero with a configurable factor (defaults give a ~60s
///         effective window).
///     </para>
/// </remarks>
public sealed class DegradationAtom : IDisposable
{
    /// <summary>Global signal key: fraction of recent requests returning 5xx.</summary>
    public const string Error5xxRate = "response.error_rate_5xx";

    /// <summary>Global signal key: fraction returning 429.</summary>
    public const string Rate429 = "response.rate_429";

    /// <summary>Global signal key: EMA of response latency in ms.</summary>
    public const string LatencyEmaMs = "response.latency_ema";

    private readonly double _alpha;
    private readonly double _decayFactor;
    private readonly ConcurrentDictionary<string, double> _emaValues = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, double> _latencyEma = new(StringComparer.Ordinal);
    private readonly Timer _decayTimer;

    public DegradationAtom(double windowSeconds = 60.0, double emaAlpha = 0.3)
    {
        _alpha = emaAlpha;
        _decayFactor = 1.0 - (emaAlpha * (1.0 / Math.Max(1.0, windowSeconds)));
        _decayTimer = new Timer(Decay, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    /// <summary>
    ///     Feed one completed response into the rolling averages. Call once
    ///     per request, typically from <c>HttpResponse.OnCompleted</c>.
    /// </summary>
    public void RecordResponse(int statusCode, long latencyMs, string path)
    {
        var is5Xx = statusCode >= 500 && statusCode < 600;
        var is429 = statusCode == 429;

        UpdateEma(Error5xxRate, is5Xx ? 1.0 : 0.0);
        UpdateEma(Rate429, is429 ? 1.0 : 0.0);
        UpdateLatencyEma(LatencyEmaMs, latencyMs);
    }

    /// <summary>
    ///     Current EMA value for one of the three global signal keys. Used
    ///     by <see cref="IAdaptiveScalingTracker"/> when picking the
    ///     active degradation tier. Returns 0.0 when no responses have
    ///     been recorded yet.
    /// </summary>
    public double GetSignalValue(string signalKey)
    {
        if (_emaValues.TryGetValue(signalKey, out var rate))
            return rate;
        if (_latencyEma.TryGetValue(signalKey, out var latency))
            return latency;
        return 0.0;
    }

    public void Dispose() => _decayTimer.Dispose();

    private void UpdateEma(string key, double sample) =>
        _emaValues.AddOrUpdate(key, sample, (_, prev) => (_alpha * sample) + ((1.0 - _alpha) * prev));

    private void UpdateLatencyEma(string key, long latencyMs) =>
        _latencyEma.AddOrUpdate(key, latencyMs, (_, prev) => (_alpha * latencyMs) + ((1.0 - _alpha) * prev));

    private void Decay(object? _)
    {
        foreach (var key in _emaValues.Keys.ToList())
            _emaValues.AddOrUpdate(key, 0.0, (_, prev) => prev * _decayFactor);
        foreach (var key in _latencyEma.Keys.ToList())
            _latencyEma.AddOrUpdate(key, 0.0, (_, prev) => prev * _decayFactor);
    }
}
