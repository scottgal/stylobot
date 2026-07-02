using FluentAssertions;
using Mostlylucid.BotDetection.Storage;

namespace Mostlylucid.BotDetection.Test.Storage;

/// <summary>
///     Unit tests for <see cref="MemoryAdaptiveCap"/> — the GC-like governor that
///     ramps a store's effective cap down as managed-memory load approaches the
///     runtime's high-load threshold. Pressure is injected so the ramp is
///     deterministic without touching real GC state.
/// </summary>
public sealed class MemoryAdaptiveCapTests
{
    private static MemoryAdaptiveCap Cap(int max, int floor, double pressure)
        // Encode a pressure ratio as (load, high) = (pressure*1e9, 1e9).
        => new(max, floor, () => ((long)(pressure * 1_000_000_000), 1_000_000_000));

    [Fact]
    public void Low_pressure_uses_full_ceiling()
        => Cap(20_000, 1_000, pressure: 0.30).Effective().Should().Be(20_000);

    [Fact]
    public void At_ramp_start_still_full_ceiling()
        => Cap(20_000, 1_000, pressure: 0.70).Effective().Should().Be(20_000);

    [Fact]
    public void At_high_threshold_collapses_to_floor()
        => Cap(20_000, 1_000, pressure: 1.00).Effective().Should().Be(1_000);

    [Fact]
    public void Above_high_threshold_holds_floor()
        => Cap(20_000, 1_000, pressure: 1.50).Effective().Should().Be(1_000);

    [Fact]
    public void Mid_ramp_is_between_floor_and_ceiling()
    {
        // pressure 0.85 -> halfway across the 0.70..1.0 ramp.
        var eff = Cap(20_000, 1_000, pressure: 0.85).Effective();
        // pressure 0.85 is halfway across the 0.70..1.0 ramp: 20000 - 0.5*(20000-1000) = 10500.
        eff.Should().BeInRange(10_300, 10_700);
    }

    [Fact]
    public void Ramp_is_monotonic_non_increasing_in_pressure()
    {
        var prev = int.MaxValue;
        for (var p = 0.0; p <= 1.2; p += 0.05)
        {
            var eff = Cap(50_000, 2_000, p).Effective();
            eff.Should().BeLessThanOrEqualTo(prev);
            prev = eff;
        }
    }

    [Fact]
    public void Unavailable_memory_info_trusts_the_ceiling()
    {
        // Some sandboxes report 0 for the GC thresholds.
        new MemoryAdaptiveCap(20_000, 1_000, () => (0, 0)).Effective().Should().Be(20_000);
    }

    [Fact]
    public void Floor_never_exceeds_ceiling()
    {
        // configuredMax below floor is clamped up to the floor.
        var cap = new MemoryAdaptiveCap(500, 1_000, () => (0, 0));
        cap.Ceiling.Should().Be(1_000);
        cap.Effective().Should().Be(1_000);
    }
}