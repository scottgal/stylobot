using FluentAssertions;
using Mostlylucid.BotDetection.Policies.Decisions;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.BotDetection.UI.Services.HealthSummaryProviders;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Coverage for the per-surface caches that pair with
///     <see cref="DashboardFreshnessBeacon"/> (issue #122).
///     Three near-identical caches; the tests are intentionally repeated
///     once per type because the DI marker subclasses must continue to
///     work as drop-in <see cref="PackHealthTileCache"/>s.
/// </summary>
public sealed class DashboardFreshnessCacheTests
{
    // ---------- PolicyStackSummaryCache ----------------------------------

    [Fact]
    public void PolicyStackSummaryCache_returns_null_before_Set()
    {
        var cache = new PolicyStackSummaryCache();
        cache.TryGet().Should().BeNull();
    }

    [Fact]
    public void PolicyStackSummaryCache_returns_stored_value_after_Set()
    {
        var cache = new PolicyStackSummaryCache();
        var summary = new PolicyStackSummary(
            TotalRules: 4,
            LiveRules: 2,
            ObserveRules: 1,
            DraftRules: 1,
            DisabledRules: 0,
            DecisionsLast15m: 12,
            HealthBand: HealthBand.Good,
            ComputedAtUtc: DateTimeOffset.UtcNow);

        cache.Set(summary);

        cache.TryGet().Should().BeSameAs(summary);
    }

    [Fact]
    public void PolicyStackSummaryCache_returns_null_after_Invalidate()
    {
        var cache = new PolicyStackSummaryCache();
        var summary = new PolicyStackSummary(
            TotalRules: 1,
            LiveRules: 1,
            ObserveRules: 0,
            DraftRules: 0,
            DisabledRules: 0,
            DecisionsLast15m: null,
            HealthBand: HealthBand.Good,
            ComputedAtUtc: DateTimeOffset.UtcNow);

        cache.Set(summary);
        cache.Invalidate();

        cache.TryGet().Should().BeNull();
    }

    [Fact]
    public void PolicyStackSummaryCache_rejects_null_set()
    {
        var cache = new PolicyStackSummaryCache();

        var act = () => cache.Set(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ---------- PackHealthTileCache (base) -------------------------------

    [Fact]
    public void PackHealthTileCache_round_trips_tile_through_Set_TryGet_Invalidate()
    {
        var cache = new PackHealthTileCache();
        cache.TryGet().Should().BeNull();

        var tile = new StatTileViewModel("X", "1");
        cache.Set(tile);

        cache.TryGet().Should().BeSameAs(tile);

        cache.Invalidate();
        cache.TryGet().Should().BeNull();
    }

    // ---------- MeterStreamHealthTileCache (marker subclass) -------------

    [Fact]
    public void MeterStreamHealthTileCache_is_a_PackHealthTileCache()
    {
        var cache = new MeterStreamHealthTileCache();
        cache.Should().BeAssignableTo<PackHealthTileCache>();
    }

    [Fact]
    public void MeterStreamHealthTileCache_round_trips_tile()
    {
        var cache = new MeterStreamHealthTileCache();
        var tile = new StatTileViewModel("Metrics", "9");
        cache.Set(tile);

        cache.TryGet().Should().BeSameAs(tile);

        cache.Invalidate();
        cache.TryGet().Should().BeNull();
    }
}
