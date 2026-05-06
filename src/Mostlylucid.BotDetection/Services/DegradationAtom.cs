using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.Services;

public sealed class DegradationAtom : IDisposable
{
    private const string GlobalErrorRate5Xx = "response.error_rate_5xx";
    private const string GlobalRate429 = "response.rate_429";
    private const string GlobalLatencyP95 = "response.latency_p95";

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

    public void RecordResponse(int statusCode, long latencyMs, string path)
    {
        var is5Xx = statusCode >= 500 && statusCode < 600;
        var is429 = statusCode == 429;

        UpdateEma(GlobalErrorRate5Xx, is5Xx ? 1.0 : 0.0);
        UpdateEma(GlobalRate429, is429 ? 1.0 : 0.0);
        UpdateLatencyEma(GlobalLatencyP95, latencyMs);

        if (!string.IsNullOrEmpty(path) && path != "/")
        {
            UpdateEma($"{GlobalErrorRate5Xx}:{path}", is5Xx ? 1.0 : 0.0);
            UpdateEma($"{GlobalRate429}:{path}", is429 ? 1.0 : 0.0);
            UpdateLatencyEma($"{GlobalLatencyP95}:{path}", latencyMs);
        }
    }

    public double GetSignalValue(string signalKey)
    {
        if (_emaValues.TryGetValue(signalKey, out var rate))
            return rate;
        if (_latencyEma.TryGetValue(signalKey, out var latency))
            return latency;
        return 0.0;
    }

    public IReadOnlyList<string> GetAvailableSignalKeys()
    {
        var keys = new List<string> { GlobalErrorRate5Xx, GlobalRate429, GlobalLatencyP95 };
        keys.AddRange(_emaValues.Keys.Where(k => k != GlobalErrorRate5Xx && k != GlobalRate429));
        keys.AddRange(_latencyEma.Keys.Where(k => k != GlobalLatencyP95));
        return keys.Distinct().ToList();
    }

    public void Dispose() => _decayTimer.Dispose();

    private void UpdateEma(string key, double sample)
    {
        _emaValues.AddOrUpdate(key, sample, (_, prev) => _alpha * sample + (1.0 - _alpha) * prev);
    }

    private void UpdateLatencyEma(string key, long latencyMs)
    {
        _latencyEma.AddOrUpdate(key, latencyMs, (_, prev) => _alpha * latencyMs + (1.0 - _alpha) * prev);
    }

    private void Decay(object? _)
    {
        foreach (var key in _emaValues.Keys.ToList())
            _emaValues.AddOrUpdate(key, 0.0, (_, prev) => prev * _decayFactor);
        foreach (var key in _latencyEma.Keys.ToList())
            _latencyEma.AddOrUpdate(key, 0.0, (_, prev) => prev * _decayFactor);
    }
}
