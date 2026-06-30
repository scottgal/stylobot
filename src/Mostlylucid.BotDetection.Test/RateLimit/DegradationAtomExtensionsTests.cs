using Mostlylucid.BotDetection.RateLimit;

namespace Mostlylucid.BotDetection.Test.RateLimit;

/// <summary>
///     Pins the 4xx/404 EWMA extensions to <see cref="DegradationAtom"/>.
///     These signals back <see cref="UpstreamHealthGate"/>'s decision so
///     status-derived bot signals can stand down during upstream outages
///     without polluting persisted centroid samples with outage shape.
/// </summary>
public class DegradationAtomExtensionsTests
{
    [Fact]
    public void RecordResponse_404_increments_NotFoundRate_signal()
    {
        var atom = new DegradationAtom();
        for (var i = 0; i < 100; i++)
            atom.RecordResponse(404, latencyMs: 5, path: "/missing");

        var rate = atom.GetSignalValue(DegradationAtom.NotFoundRate);
        Assert.True(rate > 0.9, $"expected >0.9, got {rate}");
    }

    [Fact]
    public void RecordResponse_4xx_increments_Error4xxRate_signal()
    {
        var atom = new DegradationAtom();
        for (var i = 0; i < 100; i++)
            atom.RecordResponse(403, latencyMs: 5, path: "/forbidden");

        var rate = atom.GetSignalValue(DegradationAtom.Error4xxRate);
        Assert.True(rate > 0.9, $"expected >0.9, got {rate}");
    }

    [Fact]
    public void RecordResponse_429_is_NOT_double_counted_into_Error4xxRate()
    {
        // 429 has its own existing signal (Rate429) and adaptive scaling already
        // reacts to it; we don't want it to also drag 4xx towards "outage" because
        // rate-limited responses ARE meaningful bot signals.
        var atom = new DegradationAtom();
        for (var i = 0; i < 100; i++)
            atom.RecordResponse(429, latencyMs: 5, path: "/api");

        var rate429 = atom.GetSignalValue(DegradationAtom.Rate429);
        var rate4xx = atom.GetSignalValue(DegradationAtom.Error4xxRate);
        Assert.True(rate429 > 0.9, $"expected Rate429 > 0.9, got {rate429}");
        Assert.True(rate4xx < 0.1, $"expected Error4xxRate < 0.1, got {rate4xx}");
    }

    [Fact]
    public void RecordResponse_404_also_contributes_to_Error4xxRate()
    {
        // 404 IS a 4xx; the gate is "either signal high enough flips unhealthy".
        var atom = new DegradationAtom();
        for (var i = 0; i < 100; i++)
            atom.RecordResponse(404, latencyMs: 5, path: "/missing");

        var rate4xx = atom.GetSignalValue(DegradationAtom.Error4xxRate);
        Assert.True(rate4xx > 0.9, $"expected Error4xxRate > 0.9 after 100x 404, got {rate4xx}");
    }

    [Fact]
    public void After_recovery_NotFoundRate_decays_below_threshold()
    {
        var atom = new DegradationAtom();
        for (var i = 0; i < 100; i++)
            atom.RecordResponse(404, latencyMs: 5, path: "/missing");

        // Long stream of healthy responses pushes EWMA down (alpha=0.3).
        for (var i = 0; i < 1000; i++)
            atom.RecordResponse(200, latencyMs: 5, path: "/");

        var rate = atom.GetSignalValue(DegradationAtom.NotFoundRate);
        Assert.True(rate < 0.1, $"expected <0.1 after recovery, got {rate}");
    }

    [Fact]
    public void TotalSamples_counts_every_recorded_response()
    {
        // Gate uses this for MinSampleCount: don't decide "unhealthy" on a
        // handful of early requests where one 500 swamps the EMA.
        var atom = new DegradationAtom();
        Assert.Equal(0, atom.TotalSamples);

        for (var i = 0; i < 7; i++)
            atom.RecordResponse(200, latencyMs: 5, path: "/");
        Assert.Equal(7, atom.TotalSamples);

        for (var i = 0; i < 3; i++)
            atom.RecordResponse(500, latencyMs: 5, path: "/");
        Assert.Equal(10, atom.TotalSamples);
    }

    [Fact]
    public void Existing_5xx_and_429_signals_unaffected_by_new_arms()
    {
        // Regression guard: the extension must not alter existing signal behaviour.
        var atom = new DegradationAtom();
        for (var i = 0; i < 100; i++)
            atom.RecordResponse(500, latencyMs: 20, path: "/");

        var rate5xx = atom.GetSignalValue(DegradationAtom.Error5xxRate);
        Assert.True(rate5xx > 0.9, $"expected Error5xxRate > 0.9, got {rate5xx}");
    }
}