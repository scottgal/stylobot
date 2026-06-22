using System.Collections.Concurrent;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

/// <summary>
///     Phase 5 v3 (2026-06-21) spec §2.D rule 3: convergence detection.
///     The calibration tracks per-pair counters across cycles and surfaces a
///     merge-candidate row when two archetypes stay close for N consecutive
///     cycles. Pure-function tests on
///     <see cref="IdentityWeightCalibrationService.UpdateConvergenceCounters"/>.
/// </summary>
public sealed class ConvergenceDetectionTests
{
    private static IdentityArchetype Arch(
        string id, string? nearestId, double distance) => new()
    {
        ArchetypeId = id,
        Name = id,
        ArchetypeKind = "test",
        Centroid = new float[] { 0f, 0f },
        DimensionMask = new float[] { 0f, 0f },
        NearestNeighbourId = nearestId,
        NearestNeighbourDistance = distance,
    };

    private static UmbrellaShrinkageOptions Opts(
        double threshold = 0.15, int warnAfter = 3) => new()
    {
        ConvergenceMergeThreshold = threshold,
        ConvergenceWarnAfterCycles = warnAfter,
    };

    [Fact]
    public void No_pair_within_threshold_returns_empty()
    {
        var archetypes = new[]
        {
            Arch("a", "b", 0.5),
            Arch("b", "a", 0.5),
        };
        var counters = new ConcurrentDictionary<string, int>();

        var candidates = IdentityWeightCalibrationService.UpdateConvergenceCounters(
            archetypes, Opts(threshold: 0.15), counters);

        Assert.Empty(candidates);
        Assert.Empty(counters);
    }

    [Fact]
    public void Pair_within_threshold_increments_counter_but_not_yet_a_candidate()
    {
        // Cycle 1: pair is close but only 1 cycle in. warnAfter=3 so no
        // candidate yet -- the counter exists, but no warning.
        var archetypes = new[]
        {
            Arch("a", "b", 0.10),
            Arch("b", "a", 0.10),
        };
        var counters = new ConcurrentDictionary<string, int>();

        var candidates = IdentityWeightCalibrationService.UpdateConvergenceCounters(
            archetypes, Opts(threshold: 0.15, warnAfter: 3), counters);

        Assert.Empty(candidates);
        Assert.Single(counters);
        Assert.Equal(1, counters["a|b"]);
    }

    [Fact]
    public void Pair_crossing_warn_threshold_emits_a_candidate()
    {
        var archetypes = new[]
        {
            Arch("a", "b", 0.10),
            Arch("b", "a", 0.10),
        };
        var counters = new ConcurrentDictionary<string, int>();

        // Drive 3 cycles. First two return empty; third returns the candidate.
        for (var i = 0; i < 2; i++)
        {
            var c = IdentityWeightCalibrationService.UpdateConvergenceCounters(
                archetypes, Opts(threshold: 0.15, warnAfter: 3), counters);
            Assert.Empty(c);
        }

        var candidates = IdentityWeightCalibrationService.UpdateConvergenceCounters(
            archetypes, Opts(threshold: 0.15, warnAfter: 3), counters);

        var only = Assert.Single(candidates);
        Assert.Equal("a", only.ArchetypeA);  // canonical lex-ordered pair
        Assert.Equal("b", only.ArchetypeB);
        Assert.Equal(0.10, only.Distance, precision: 6);
        Assert.Equal(3, only.Cycles);
    }

    [Fact]
    public void Counter_resets_when_pair_drifts_outside_threshold()
    {
        // Pin: a noisy pair must reset cleanly. Cycle 1: close. Cycle 2: far.
        // Counter for the pair MUST be dropped, so a subsequent close cycle
        // starts counting from 1 again.
        var counters = new ConcurrentDictionary<string, int>();
        var opts = Opts(threshold: 0.15, warnAfter: 3);

        // Cycle 1: close.
        IdentityWeightCalibrationService.UpdateConvergenceCounters(
            new[] { Arch("a", "b", 0.10), Arch("b", "a", 0.10) }, opts, counters);
        Assert.Equal(1, counters["a|b"]);

        // Cycle 2: far.
        IdentityWeightCalibrationService.UpdateConvergenceCounters(
            new[] { Arch("a", "b", 0.50), Arch("b", "a", 0.50) }, opts, counters);
        Assert.Empty(counters);

        // Cycle 3: close again -- counter starts from 1.
        IdentityWeightCalibrationService.UpdateConvergenceCounters(
            new[] { Arch("a", "b", 0.10), Arch("b", "a", 0.10) }, opts, counters);
        Assert.Equal(1, counters["a|b"]);
    }

    [Fact]
    public void Pair_keys_are_canonical_regardless_of_iteration_order()
    {
        // Pin: the per-pair counter uses a deterministic key. Two archetypes
        // viewing each other as nearest neighbours MUST update the same
        // counter, not double-count. The annotation lists BOTH directions
        // (a->b and b->a) every cycle; we should still see ONE counter.
        var counters = new ConcurrentDictionary<string, int>();
        var opts = Opts(threshold: 0.15, warnAfter: 3);

        IdentityWeightCalibrationService.UpdateConvergenceCounters(
            new[] { Arch("alpha", "beta", 0.10), Arch("beta", "alpha", 0.10) },
            opts, counters);

        Assert.Single(counters);
        Assert.Equal(1, counters.Values.Single());
    }

    [Fact]
    public void Counter_continues_past_warn_threshold_so_the_candidate_ages()
    {
        // Pin: candidate.Cycles keeps growing past warnAfter so the dashboard
        // can show "this has been pinned for 12 cycles". The warning logs on
        // the BOUNDARY only (caller's responsibility), but the candidate
        // record keeps fresh count.
        var archetypes = new[]
        {
            Arch("a", "b", 0.10),
            Arch("b", "a", 0.10),
        };
        var counters = new ConcurrentDictionary<string, int>();
        var opts = Opts(threshold: 0.15, warnAfter: 3);

        for (var i = 0; i < 4; i++)
            IdentityWeightCalibrationService.UpdateConvergenceCounters(archetypes, opts, counters);

        var candidates = IdentityWeightCalibrationService.UpdateConvergenceCounters(
            archetypes, opts, counters);
        Assert.Equal(5, candidates.Single().Cycles);
    }

    [Fact]
    public void Disabled_via_zero_threshold_returns_empty_and_does_not_touch_counters()
    {
        var archetypes = new[]
        {
            Arch("a", "b", 0.05),
            Arch("b", "a", 0.05),
        };
        var counters = new ConcurrentDictionary<string, int>();

        var candidates = IdentityWeightCalibrationService.UpdateConvergenceCounters(
            archetypes, Opts(threshold: 0), counters);

        Assert.Empty(candidates);
        Assert.Empty(counters);
    }
}
