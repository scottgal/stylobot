using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

/// <summary>
///     Pin the Phase 3 umbrella-shrinkage action ladder
///     (<see cref="IdentityWeightCalibrationService.ApplyUmbrellaShrinkage"/>).
///     The compute is pure; tests drive it directly so the action-ladder
///     transitions are observable without running the full calibration loop.
///     See spec §2.C.
/// </summary>
public sealed class UmbrellaShrinkageTests
{
    private static IdentityArchetype Arch(
        string id,
        string? assertedUaFamily = null,
        double varianceMultiplier = 1.0) => new()
        {
            ArchetypeId = id,
            Name = id,
            ArchetypeKind = "test",
            Centroid = new float[] { 0f, 0f },
            DimensionMask = new float[] { 0f, 0f },
            AssertedUaFamily = assertedUaFamily,
            VarianceMultiplier = varianceMultiplier,
        };

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

    private static UmbrellaShrinkageOptions Opts(
        double bloatShrink = 1.0,
        double splitCandidate = 2.0,
        double shrinkRate = 0.05,
        double floor = 0.05,
        double radiusBaseline = 0.5) => new()
        {
            BloatShrinkThreshold = bloatShrink,
            SplitCandidateThreshold = splitCandidate,
            ShrinkRate = shrinkRate,
            Floor = floor,
            RadiusBaseline = radiusBaseline,
        };

    [Fact]
    public void Stable_archetype_with_no_leakage_does_not_shrink()
    {
        // Pin: matches-only drift well below the bloat threshold = NO action.
        // We don't want to pessimise archetypes that are correctly sized.
        var archetypes = new[] { Arch("chrome-desktop", "Chrome") };
        var metrics = new[] { Metric("chrome-desktop", "Chrome", matches: true, count: 5, p90: 0.2) };

        var (rewritten, actions) = IdentityWeightCalibrationService
            .ApplyUmbrellaShrinkage(archetypes, metrics, Opts());

        Assert.Empty(actions);
        Assert.Equal(1.0, rewritten.Single().VarianceMultiplier);
    }

    [Fact]
    public void Leakage_alone_triggers_shrinkage_even_with_low_bloat()
    {
        // Pin: ANY matches_asserted_ua=false count triggers a (mild) shrink.
        // This is the "umbrella is catching foreign UAs" signal -- spec §2.C
        // bottom of the ladder ("non-trivial count -> shrink + lower
        // asserted-UA confidence"). In v1 we just shrink; the asserted-UA
        // confidence knob lands in v2.
        var archetypes = new[] { Arch("curl-tool", "curl") };
        var metrics = new[]
        {
            Metric("curl-tool", "curl",     matches: true,  count: 5, p90: 0.1),  // bloat << 1
            Metric("curl-tool", "Chrome",   matches: false, count: 6, p90: 0.7),  // umbrella leakage
        };

        var (rewritten, actions) = IdentityWeightCalibrationService
            .ApplyUmbrellaShrinkage(archetypes, metrics, Opts());

        var act = Assert.Single(actions).Value;
        Assert.Equal(6, act.LeakageDescendantCount);
        Assert.False(act.SplitCandidate);
        Assert.Equal(0.95, act.NewMultiplier, precision: 6);  // 1.0 * (1 - 0.05)
        Assert.Equal(0.95, rewritten.Single().VarianceMultiplier, precision: 6);
    }

    [Fact]
    public void Bloat_above_threshold_triggers_shrinkage_without_leakage()
    {
        // Pin: matching-UA p90 above the bloat ceiling is ALSO a shrink signal
        // -- the archetype claims the right UA but its descendants are too
        // spread out, so the centroid is in the wrong region of the space.
        var archetypes = new[] { Arch("chrome-desktop", "Chrome") };
        var metrics = new[] { Metric("chrome-desktop", "Chrome", matches: true, count: 8, p90: 0.6) };

        // p90 0.6 / radius 0.5 = 1.2 bloat -> above the 1.0 threshold.
        var (_, actions) = IdentityWeightCalibrationService
            .ApplyUmbrellaShrinkage(archetypes, metrics, Opts());

        var act = Assert.Single(actions).Value;
        Assert.Equal(0, act.LeakageDescendantCount);
        Assert.False(act.SplitCandidate);
        Assert.Equal(1.2, act.Bloat, precision: 6);
        Assert.Equal(0.95, act.NewMultiplier, precision: 6);
    }

    [Fact]
    public void High_bloat_flags_split_candidate()
    {
        // Pin: at 2x radius the calibration emits a split-candidate signal.
        // The action still shrinks (we always tighten on bloat), but the
        // SplitCandidate flag tells the operator the YAML catalogue may
        // need a new archetype. Phase 3 v1 doesn't auto-split.
        var archetypes = new[] { Arch("mastodon", "Mastodon") };
        var metrics = new[] { Metric("mastodon", "Mastodon", matches: true, count: 12, p90: 1.1) };

        // bloat = 1.1 / 0.5 = 2.2 -> above the 2.0 split threshold.
        var (_, actions) = IdentityWeightCalibrationService
            .ApplyUmbrellaShrinkage(archetypes, metrics, Opts());

        var act = Assert.Single(actions).Value;
        Assert.True(act.SplitCandidate);
        Assert.Equal(2.2, act.Bloat, precision: 6);
    }

    [Fact]
    public void Repeated_shrinkage_compounds_and_clamps_to_floor()
    {
        // The killer property: across N cycles, an archetype that keeps
        // tripping leakage tightens further and further -- but is floored so
        // it never vanishes. Pin both shapes in one test: the compound, and
        // the floor clamp.
        var current = new[] { Arch("umbrella", "X") };
        var leak = new[]
        {
            Metric("umbrella", "X", matches: true,  count: 1, p90: 0.1),
            Metric("umbrella", "Y", matches: false, count: 5, p90: 0.5),
        };
        var opts = Opts(shrinkRate: 0.5, floor: 0.1);  // crank for fast convergence

        // Cycle 1: 1.0 * 0.5 = 0.5
        (var c1, var a1) = IdentityWeightCalibrationService.ApplyUmbrellaShrinkage(current, leak, opts);
        Assert.Equal(0.5, c1.Single().VarianceMultiplier, precision: 6);

        // Cycle 2: 0.5 * 0.5 = 0.25
        (var c2, _) = IdentityWeightCalibrationService.ApplyUmbrellaShrinkage(c1, leak, opts);
        Assert.Equal(0.25, c2.Single().VarianceMultiplier, precision: 6);

        // Cycle 3: 0.25 * 0.5 = 0.125
        (var c3, _) = IdentityWeightCalibrationService.ApplyUmbrellaShrinkage(c2, leak, opts);
        Assert.Equal(0.125, c3.Single().VarianceMultiplier, precision: 6);

        // Cycle 4: would be 0.0625 but the 0.1 floor clamps to 0.1.
        (var c4, _) = IdentityWeightCalibrationService.ApplyUmbrellaShrinkage(c3, leak, opts);
        Assert.Equal(0.1, c4.Single().VarianceMultiplier, precision: 6);

        // Cycle 5: stays at the floor -- never goes below.
        (var c5, _) = IdentityWeightCalibrationService.ApplyUmbrellaShrinkage(c4, leak, opts);
        Assert.Equal(0.1, c5.Single().VarianceMultiplier, precision: 6);
    }

    [Fact]
    public void No_metrics_at_all_does_not_shrink_but_bumps_healthy_cycles()
    {
        // Pin: a quiet cycle (no drift metrics at all) is NOT a no-op anymore --
        // Phase 5 uses it as evidence the archetype is behaving healthily, so
        // HealthyCycles increments. Multiplier stays at 1.0 (no regrowth needed)
        // and no action is recorded (only shrinks / regrows emit actions).
        var archetypes = new[] { Arch("a"), Arch("b") };

        var (rewritten, actions) = IdentityWeightCalibrationService.ApplyUmbrellaShrinkage(
            archetypes, Array.Empty<ArchetypeDriftMetric>(), Opts());

        Assert.Empty(actions);
        Assert.All(rewritten, a => Assert.Equal(1.0, a.VarianceMultiplier));
        Assert.All(rewritten, a => Assert.Equal(1, a.HealthyCycles));
    }

    [Fact]
    public void Archetypes_without_metrics_are_untouched()
    {
        // Pin: in a corpus of many archetypes only those WITH drift metrics
        // get evaluated. Quiet archetypes stay at their current multiplier --
        // matters for the regrowth question (Phase 4) but for now the floor
        // is sticky: an archetype that escapes leakage stops shrinking.
        var archetypes = new[]
        {
            Arch("noisy",  "X", varianceMultiplier: 1.0),
            Arch("quiet",  "Y", varianceMultiplier: 0.4),  // earlier-shrunken
        };
        var metrics = new[]
        {
            Metric("noisy", "Z", matches: false, count: 3, p90: 0.3),
        };

        var (rewritten, actions) = IdentityWeightCalibrationService
            .ApplyUmbrellaShrinkage(archetypes, metrics, Opts());

        Assert.Single(actions);
        Assert.Contains("noisy", actions.Keys);

        Assert.Equal(0.95, rewritten.Single(a => a.ArchetypeId == "noisy").VarianceMultiplier, precision: 6);
        Assert.Equal(0.4,  rewritten.Single(a => a.ArchetypeId == "quiet").VarianceMultiplier, precision: 6);
    }
}
