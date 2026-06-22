using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

/// <summary>
///     Phase 5 (2026-06-21): auto-regrowth pin. After N consecutive healthy
///     cycles (no leakage, bloat below threshold), a previously-shrunken
///     archetype walks <see cref="IdentityArchetype.VarianceMultiplier"/>
///     back toward 1.0 by <see cref="UmbrellaShrinkageOptions.RegrowRate"/>
///     per cycle. Floor at 1.0 (we never widen beyond the YAML-seeded
///     catchment). Resets to 0 on any subsequent shrink.
/// </summary>
public sealed class UmbrellaRegrowthTests
{
    private static IdentityArchetype Arch(
        string id,
        string? assertedUaFamily = null,
        double varianceMultiplier = 1.0,
        int healthyCycles = 0) => new()
        {
            ArchetypeId = id,
            Name = id,
            ArchetypeKind = "test",
            Centroid = new float[] { 0f, 0f },
            DimensionMask = new float[] { 0f, 0f },
            AssertedUaFamily = assertedUaFamily,
            VarianceMultiplier = varianceMultiplier,
            HealthyCycles = healthyCycles,
        };

    private static UmbrellaShrinkageOptions Opts(
        int regrowAfterCycles = 3,
        double regrowRate = 0.10,
        double shrinkRate = 0.5) => new()
        {
            BloatShrinkThreshold = 1.0,
            SplitCandidateThreshold = 2.0,
            ShrinkRate = shrinkRate,
            Floor = 0.05,
            RadiusBaseline = 0.5,
            RegrowAfterCycles = regrowAfterCycles,
            RegrowRate = regrowRate,
        };

    [Fact]
    public void Healthy_cycle_increments_counter_without_regrowing_immediately()
    {
        // Below the regrow-after threshold: healthy cycle counts but doesn't
        // unwind multiplier. Pins the bookkeeping, not the action.
        var archetypes = new[] { Arch("a", "X", varianceMultiplier: 0.5, healthyCycles: 0) };
        var (rewritten, actions) = IdentityWeightCalibrationService.ApplyUmbrellaShrinkage(
            archetypes, Array.Empty<ArchetypeDriftMetric>(), Opts(regrowAfterCycles: 3));

        Assert.Empty(actions);
        Assert.Equal(1, rewritten.Single().HealthyCycles);
        Assert.Equal(0.5, rewritten.Single().VarianceMultiplier);
    }

    [Fact]
    public void Crossing_regrow_threshold_emits_a_regrow_action()
    {
        // Pin: once HealthyCycles >= RegrowAfterCycles AND the multiplier is
        // still below 1.0, we walk it up by RegrowRate. The action records
        // this so the dashboard shows regrowth happening, not just shrinkage.
        var archetypes = new[] { Arch("a", "X", varianceMultiplier: 0.5, healthyCycles: 2) };
        var (rewritten, actions) = IdentityWeightCalibrationService.ApplyUmbrellaShrinkage(
            archetypes, Array.Empty<ArchetypeDriftMetric>(), Opts(regrowAfterCycles: 3, regrowRate: 0.10));

        var act = Assert.Single(actions).Value;
        Assert.Equal("regrow", act.Kind);
        Assert.Equal(0.5, act.PreviousMultiplier);
        Assert.Equal(0.55, act.NewMultiplier, precision: 6);  // 0.5 * 1.10
        Assert.Equal(0.55, rewritten.Single().VarianceMultiplier, precision: 6);
        Assert.Equal(3, rewritten.Single().HealthyCycles);
    }

    [Fact]
    public void Regrowth_clamps_at_one_so_we_never_widen_beyond_yaml_seed()
    {
        // Pin: regrowth has the same kind of safety stop as shrinkage. The
        // YAML-seeded mask is the operator's intent; we never exceed it even
        // after a long healthy run.
        var archetypes = new[] { Arch("a", "X", varianceMultiplier: 0.95, healthyCycles: 10) };
        var (rewritten, _) = IdentityWeightCalibrationService.ApplyUmbrellaShrinkage(
            archetypes, Array.Empty<ArchetypeDriftMetric>(), Opts(regrowAfterCycles: 3, regrowRate: 0.5));

        Assert.Equal(1.0, rewritten.Single().VarianceMultiplier);
    }

    [Fact]
    public void Already_at_one_does_not_emit_a_regrow_action()
    {
        // Pin: an archetype at the cap with no shrink condition is just quiet.
        // We bump HealthyCycles but don't emit an action -- the dashboard
        // shouldn't fill with no-op regrowth rows for well-behaved archetypes.
        var archetypes = new[] { Arch("a", "X", varianceMultiplier: 1.0, healthyCycles: 10) };
        var (rewritten, actions) = IdentityWeightCalibrationService.ApplyUmbrellaShrinkage(
            archetypes, Array.Empty<ArchetypeDriftMetric>(), Opts(regrowAfterCycles: 3, regrowRate: 0.10));

        Assert.Empty(actions);
        Assert.Equal(1.0, rewritten.Single().VarianceMultiplier);
        Assert.Equal(11, rewritten.Single().HealthyCycles);
    }

    [Fact]
    public void Any_shrink_event_resets_healthy_cycles_to_zero()
    {
        // Pin: a single leakage event nukes the regrowth progress. We want
        // sustained quiet, not "5 healthy / 1 noisy / 5 healthy" oscillation.
        var archetypes = new[] { Arch("a", "X", varianceMultiplier: 0.6, healthyCycles: 4) };
        var metrics = new[] { Metric("a", "Y", matches: false, count: 3, p90: 0.1) };

        var (rewritten, actions) = IdentityWeightCalibrationService.ApplyUmbrellaShrinkage(
            archetypes, metrics, Opts(shrinkRate: 0.5));

        var act = Assert.Single(actions).Value;
        Assert.Equal("shrink", act.Kind);
        Assert.Equal(0, rewritten.Single().HealthyCycles);
    }

    [Fact]
    public void Compound_regrowth_walks_steadily_back_toward_one()
    {
        // The full lifecycle test: archetype shrunk to 0.3, then runs healthy
        // for 5 cycles with regrowAfter=3 / rate=0.20 / shrinkRate=0.5.
        //   t0: 0.3, healthy=0
        //   t1: 0.3, healthy=1
        //   t2: 0.3, healthy=2
        //   t3: 0.30 * 1.20 = 0.36, healthy=3
        //   t4: 0.36 * 1.20 = 0.432, healthy=4
        //   t5: 0.432 * 1.20 = 0.5184, healthy=5
        var opts = Opts(regrowAfterCycles: 3, regrowRate: 0.20);
        IReadOnlyList<IdentityArchetype> current = new IdentityArchetype[]
        {
            Arch("a", "X", varianceMultiplier: 0.3, healthyCycles: 0),
        };

        var expected = new[] { 0.30, 0.30, 0.30, 0.36, 0.432, 0.5184 };
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], current.Single().VarianceMultiplier, precision: 6);
            (current, _) = IdentityWeightCalibrationService.ApplyUmbrellaShrinkage(
                current, Array.Empty<ArchetypeDriftMetric>(), opts);
        }
    }

    private static ArchetypeDriftMetric Metric(
        string archetypeId, string ua, bool matches, int count, double p90) => new()
        {
            ArchetypeId = archetypeId,
            UaFamily = ua,
            MatchesAssertedUa = matches,
            DescendantCount = count,
            MeanL2ToCentroid = p90 * 0.8,
            VarianceL2ToCentroid = 0,
            P90L2ToCentroid = p90,
            CalibratedAt = DateTime.UtcNow,
        };
}
