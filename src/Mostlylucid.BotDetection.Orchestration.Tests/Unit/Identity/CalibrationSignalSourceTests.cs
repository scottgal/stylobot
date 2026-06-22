using Mostlylucid.BotDetection.Identity.Triggers;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

/// <summary>
///     Coverage for the increment / accumulate / snapshot / reset cycle of
///     <see cref="CalibrationSignalSource"/>. The signal source is the one
///     hot-path-touched piece of the adaptive trigger; these tests pin its
///     contract so a future refactor of the storage representation can't
///     silently lose increments or fail to reset.
/// </summary>
public sealed class CalibrationSignalSourceTests
{
    [Fact]
    public void Empty_source_snapshot_reports_zero_for_both_signals()
    {
        var src = new CalibrationSignalSource();
        var snap = src.Snapshot();
        Assert.Equal(0.0, snap[CalibrationSignalSource.SignalKeys.ObsUnabsorbed]);
        Assert.Equal(0.0, snap[CalibrationSignalSource.SignalKeys.DriftL2]);
    }

    [Fact]
    public void OnObservation_increments_the_obs_signal()
    {
        var src = new CalibrationSignalSource();
        for (var i = 0; i < 17; i++) src.OnObservation();
        var snap = src.Snapshot();
        Assert.Equal(17.0, snap[CalibrationSignalSource.SignalKeys.ObsUnabsorbed]);
    }

    [Fact]
    public void OnAbsorption_accumulates_drift_l2()
    {
        var src = new CalibrationSignalSource();
        src.OnAbsorption(0.10);
        src.OnAbsorption(0.25);
        src.OnAbsorption(0.40);
        var snap = src.Snapshot();
        Assert.Equal(0.75, snap[CalibrationSignalSource.SignalKeys.DriftL2], precision: 6);
    }

    [Fact]
    public void OnAbsorption_clamps_negative_and_nan_to_zero()
    {
        // Defensive: the absorption path computes L2 from float arrays so a
        // pathological input could in principle produce NaN. The signal must
        // never go non-finite or negative -- the trigger comparisons would
        // misbehave silently.
        var src = new CalibrationSignalSource();
        src.OnAbsorption(double.NaN);
        src.OnAbsorption(-1.5);
        src.OnAbsorption(0.0);
        src.OnAbsorption(0.3);
        var snap = src.Snapshot();
        Assert.Equal(0.3, snap[CalibrationSignalSource.SignalKeys.DriftL2], precision: 6);
    }

    [Fact]
    public void Reset_returns_both_signals_to_zero()
    {
        var src = new CalibrationSignalSource();
        for (var i = 0; i < 50; i++) src.OnObservation();
        src.OnAbsorption(2.5);

        var before = src.Snapshot();
        Assert.Equal(50.0, before[CalibrationSignalSource.SignalKeys.ObsUnabsorbed]);
        Assert.Equal(2.5, before[CalibrationSignalSource.SignalKeys.DriftL2], precision: 6);

        src.Reset();

        var after = src.Snapshot();
        Assert.Equal(0.0, after[CalibrationSignalSource.SignalKeys.ObsUnabsorbed]);
        Assert.Equal(0.0, after[CalibrationSignalSource.SignalKeys.DriftL2]);
    }

    [Fact]
    public async Task Concurrent_OnObservation_increments_are_not_lost()
    {
        // 16 tasks × 1000 increments each. Lossy increment (non-Interlocked)
        // would lose ~10% under contention; the test would fail noisily.
        var src = new CalibrationSignalSource();
        const int tasksCount = 16;
        const int perTask = 1000;

        await Task.WhenAll(Enumerable.Range(0, tasksCount).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < perTask; i++) src.OnObservation();
        })));

        var snap = src.Snapshot();
        Assert.Equal((double)(tasksCount * perTask),
            snap[CalibrationSignalSource.SignalKeys.ObsUnabsorbed]);
    }

    [Fact]
    public async Task Concurrent_OnAbsorption_drift_sum_is_within_tolerance()
    {
        // Each task adds 1000 × 0.001 = 1.0; 16 tasks → expected 16.0. The CAS
        // loop guarantees the sum is exact under no-contention reads but
        // double-precision accumulation gives a tiny rounding tolerance.
        var src = new CalibrationSignalSource();
        const int tasksCount = 16;
        const int perTask = 1000;

        await Task.WhenAll(Enumerable.Range(0, tasksCount).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < perTask; i++) src.OnAbsorption(0.001);
        })));

        var snap = src.Snapshot();
        Assert.Equal(16.0, snap[CalibrationSignalSource.SignalKeys.DriftL2], precision: 3);
    }
}
