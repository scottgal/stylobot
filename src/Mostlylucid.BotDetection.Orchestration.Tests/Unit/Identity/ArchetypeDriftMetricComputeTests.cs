using Mostlylucid.BotDetection.Identity;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

/// <summary>
///     Pure-function coverage for
///     <see cref="IdentityWeightCalibrationService.ComputeDriftMetrics"/>. Pin
///     the grouping, statistics, and the load-bearing
///     <see cref="ArchetypeDriftMetric.MatchesAssertedUa"/> flag without
///     spinning up SQLite. See spec §2.B.
/// </summary>
public sealed class ArchetypeDriftMetricComputeTests
{
    private static IdentityArchetype Archetype(
        string id,
        float[] centroid,
        string? assertedUaFamily = null) => new()
        {
            ArchetypeId = id,
            Name = id,
            ArchetypeKind = "test",
            Centroid = centroid,
            DimensionMask = new float[centroid.Length],
            AssertedUaFamily = assertedUaFamily,
        };

    [Fact]
    public void Empty_input_returns_empty_output()
    {
        var metrics = IdentityWeightCalibrationService.ComputeDriftMetrics(
            Array.Empty<DriftObservationRow>(),
            Array.Empty<IdentityArchetype>());
        Assert.Empty(metrics);
    }

    [Fact]
    public void Single_archetype_single_ua_aggregates_into_one_row()
    {
        var arch = Archetype("chrome-desktop", new float[] { 0f, 0f }, assertedUaFamily: "Chrome");
        var rows = new[]
        {
            new DriftObservationRow("chrome-desktop", "Chrome", new float[] { 1f, 0f }),    // L2 = 1
            new DriftObservationRow("chrome-desktop", "Chrome", new float[] { 0f, 1f }),    // L2 = 1
            new DriftObservationRow("chrome-desktop", "Chrome", new float[] { 1f, 1f }),    // L2 = sqrt(2)
        };
        var metrics = IdentityWeightCalibrationService.ComputeDriftMetrics(rows, new[] { arch });

        var single = Assert.Single(metrics);
        Assert.Equal("chrome-desktop", single.ArchetypeId);
        Assert.Equal("Chrome", single.UaFamily);
        Assert.True(single.MatchesAssertedUa);
        Assert.Equal(3, single.DescendantCount);
        Assert.Equal((2 + Math.Sqrt(2)) / 3, single.MeanL2ToCentroid, precision: 6);
        // p90 of 3 sorted values [1, 1, sqrt(2)] is the index ceil(0.9*3)-1 = 2 -> sqrt(2).
        Assert.Equal(Math.Sqrt(2), single.P90L2ToCentroid, precision: 6);
    }

    [Fact]
    public void Multiple_uas_split_into_separate_rows_with_distinct_match_flags()
    {
        // Pin the load-bearing case: archetype claims Chrome. Half the obs are
        // Chrome (matches_asserted_ua=true), half are Googlebot (false). The
        // mismatch row is the umbrella-leakage signal Phase 3 will act on.
        var arch = Archetype("chrome-desktop", new float[] { 0f, 0f }, assertedUaFamily: "Chrome");
        var rows = new[]
        {
            new DriftObservationRow("chrome-desktop", "Chrome", new float[] { 1f, 0f }),
            new DriftObservationRow("chrome-desktop", "Chrome", new float[] { 0f, 1f }),
            new DriftObservationRow("chrome-desktop", "Googlebot", new float[] { 5f, 0f }),
            new DriftObservationRow("chrome-desktop", "Googlebot", new float[] { 0f, 5f }),
        };

        var metrics = IdentityWeightCalibrationService.ComputeDriftMetrics(rows, new[] { arch });

        Assert.Equal(2, metrics.Count);
        var chromeRow = metrics.Single(m => m.UaFamily == "Chrome");
        var leakRow = metrics.Single(m => m.UaFamily == "Googlebot");

        Assert.True(chromeRow.MatchesAssertedUa);
        Assert.False(leakRow.MatchesAssertedUa);
        Assert.Equal(2, chromeRow.DescendantCount);
        Assert.Equal(2, leakRow.DescendantCount);

        // Leakage row drift should be visibly larger; that's the whole point.
        Assert.True(leakRow.MeanL2ToCentroid > chromeRow.MeanL2ToCentroid,
            $"leak mean ({leakRow.MeanL2ToCentroid:F2}) should exceed matching ({chromeRow.MeanL2ToCentroid:F2})");
    }

    [Fact]
    public void Archetype_with_no_asserted_ua_never_reports_matches_asserted_ua_true()
    {
        // Pin: AssertedUaFamily = null on the archetype means "no UA claim".
        // matches_asserted_ua MUST stay false even when descendants share a UA --
        // the flag's meaning is "this archetype claims a UA family and the
        // descendant agrees", not "all descendants share a UA".
        var arch = Archetype("uncategorised", new float[] { 0f, 0f }, assertedUaFamily: null);
        var rows = new[]
        {
            new DriftObservationRow("uncategorised", "Chrome", new float[] { 1f, 0f }),
        };
        var metrics = IdentityWeightCalibrationService.ComputeDriftMetrics(rows, new[] { arch });
        Assert.False(metrics.Single().MatchesAssertedUa);
    }

    [Fact]
    public void Observations_for_unknown_archetypes_are_dropped()
    {
        // A descendant whose inferred_client_type is no longer in the registry
        // (archetype was removed mid-cycle) MUST NOT crash compute. We drop the
        // row silently -- there's no centroid to compare against.
        var arch = Archetype("known", new float[] { 0f, 0f });
        var rows = new[]
        {
            new DriftObservationRow("known",   "Chrome", new float[] { 1f, 0f }),
            new DriftObservationRow("missing", "Chrome", new float[] { 1f, 0f }),
        };
        var metrics = IdentityWeightCalibrationService.ComputeDriftMetrics(rows, new[] { arch });
        Assert.Single(metrics);
        Assert.Equal("known", metrics[0].ArchetypeId);
    }

    [Fact]
    public void Empty_ua_family_is_a_distinct_bucket_from_named_ua()
    {
        var arch = Archetype("chrome-desktop", new float[] { 0f, 0f }, assertedUaFamily: "Chrome");
        var rows = new[]
        {
            new DriftObservationRow("chrome-desktop", "",       new float[] { 1f, 0f }),
            new DriftObservationRow("chrome-desktop", "Chrome", new float[] { 1f, 0f }),
        };
        var metrics = IdentityWeightCalibrationService.ComputeDriftMetrics(rows, new[] { arch });
        Assert.Equal(2, metrics.Count);
        Assert.Contains(metrics, m => m.UaFamily == "" && !m.MatchesAssertedUa);
        Assert.Contains(metrics, m => m.UaFamily == "Chrome" && m.MatchesAssertedUa);
    }

    [Fact]
    public void Variance_is_zero_for_a_single_observation_bucket()
    {
        // Sample variance with N=1 is undefined; we choose 0 (the design spec
        // calls this out). Pin so a future "use population variance" tweak
        // doesn't silently change the row's meaning.
        var arch = Archetype("a", new float[] { 0f, 0f });
        var rows = new[] { new DriftObservationRow("a", "X", new float[] { 1f, 0f }) };
        var metrics = IdentityWeightCalibrationService.ComputeDriftMetrics(rows, new[] { arch });
        Assert.Equal(0, metrics.Single().VarianceL2ToCentroid);
    }
}
