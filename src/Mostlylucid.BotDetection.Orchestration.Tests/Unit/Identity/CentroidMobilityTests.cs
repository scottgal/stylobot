using Mostlylucid.BotDetection.Identity;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

/// <summary>
///     Phase 4 (2026-06-21) spec §2.D centroid mobility: pin tracking,
///     adaptive &#x3B1;, nearest-neighbour annotation. The compute is pure so
///     each rule lives in its own test without the calibration loop.
/// </summary>
public sealed class CentroidMobilityTests
{
    // ----- AdaptiveAlphaFromVariance ----------------------------------------

    [Fact]
    public void Adaptive_alpha_returns_cap_when_variance_is_zero()
    {
        // Pin: descendants agree perfectly -> use the full refinement cap.
        // This is the steady-state behaviour and the pre-Phase-4 default.
        var alpha = IdentityWeightMath.AdaptiveAlphaFromVariance(
            alphaCap: 0.7, alphaMin: 0.2, meanVariance: 0, varianceScale: 0.1);
        Assert.Equal(0.7, alpha, precision: 6);
    }

    [Fact]
    public void Adaptive_alpha_clamps_at_floor_for_very_high_variance()
    {
        // Pin: when descendants spread out, alpha drops toward AlphaMin.
        // Spec §2.D rule 1: let umbrella shrinkage purge the misassigned
        // descendants rather than chasing their inconsistent mean.
        var alpha = IdentityWeightMath.AdaptiveAlphaFromVariance(
            alphaCap: 0.7, alphaMin: 0.2, meanVariance: 100, varianceScale: 0.1);
        Assert.Equal(0.2, alpha, precision: 6);
    }

    [Fact]
    public void Adaptive_alpha_is_monotonic_in_variance()
    {
        // Pin: as variance rises, alpha never goes UP. Catches a sign-error
        // that would invert the spec rule (high-variance -> chase-the-mean).
        double prev = double.PositiveInfinity;
        foreach (var v in new[] { 0.0, 0.01, 0.05, 0.1, 0.5, 1.0, 5.0 })
        {
            var a = IdentityWeightMath.AdaptiveAlphaFromVariance(
                alphaCap: 0.7, alphaMin: 0.2, meanVariance: v, varianceScale: 0.1);
            Assert.True(a <= prev + 1e-9, $"alpha rose at variance {v}: {prev:F4} -> {a:F4}");
            prev = a;
        }
    }

    [Fact]
    public void Adaptive_alpha_zero_scale_short_circuits_to_cap()
    {
        // Defensive: a misconfigured scale of 0 would div-by-zero in exp(-var/0).
        // The util returns the cap; the calibration runs normally.
        var alpha = IdentityWeightMath.AdaptiveAlphaFromVariance(
            alphaCap: 0.7, alphaMin: 0.2, meanVariance: 0.1, varianceScale: 0);
        Assert.Equal(0.7, alpha);
    }

    // ----- RefineArchetypeCentroidAdaptive ----------------------------------

    [Fact]
    public void Refinement_with_zero_alpha_returns_input_centroid_and_zero_delta()
    {
        var centroid = new float[] { 1f, 0f, 0f };
        var descendants = new float[][] { new[] { 0f, 1f, 0f } };

        var result = IdentityWeightMath.RefineArchetypeCentroidAdaptive(
            centroid, descendants, alpha: 0.0);
        Assert.NotNull(result);
        Assert.Equal(0, result.Value.DeltaL2, precision: 6);
        Assert.Equal(centroid[0], result.Value.Refined[0], precision: 6);
    }

    [Fact]
    public void Refinement_computes_mean_variance_from_descendants()
    {
        // Two descendants at opposite ends on dim 0; variance per dim = 0.5*(1-0)^2 + 0.5*(-1-0)^2 with sample (N-1) denom = 2.
        // Across 3 dims: only dim 0 has variance; mean = 2/3.
        var centroid = new float[] { 0f, 0f, 0f };
        var descendants = new float[][]
        {
            new float[] { 1f, 0f, 0f },
            new float[] { -1f, 0f, 0f },
        };
        var result = IdentityWeightMath.RefineArchetypeCentroidAdaptive(
            centroid, descendants, alpha: 0.5);
        Assert.NotNull(result);
        Assert.Equal(2.0 / 3.0, result.Value.MeanVariance, precision: 6);
    }

    [Fact]
    public void Refinement_with_no_descendants_returns_null()
    {
        var centroid = new float[] { 1f, 0f, 0f };
        var result = IdentityWeightMath.RefineArchetypeCentroidAdaptive(
            centroid, Array.Empty<float[]>(), alpha: 0.5);
        Assert.Null(result);
    }

    // ----- AnnotateNearestNeighbours ----------------------------------------

    [Fact]
    public void Nearest_neighbour_pairs_each_archetype_with_its_closest_other()
    {
        var a = Arch("a", new float[] { 0f, 0f });
        var b = Arch("b", new float[] { 1f, 0f });
        var c = Arch("c", new float[] { 10f, 0f });

        var annotated = IdentityWeightCalibrationService
            .AnnotateNearestNeighbours(new[] { a, b, c });

        // a's nearest is b (distance 1, c is 10 away)
        var aOut = annotated.Single(x => x.ArchetypeId == "a");
        Assert.Equal("b", aOut.NearestNeighbourId);
        Assert.Equal(1.0, aOut.NearestNeighbourDistance, precision: 6);

        // b's nearest is a (distance 1, c is 9 away)
        var bOut = annotated.Single(x => x.ArchetypeId == "b");
        Assert.Equal("a", bOut.NearestNeighbourId);
        Assert.Equal(1.0, bOut.NearestNeighbourDistance, precision: 6);

        // c's nearest is b (distance 9, a is 10 away)
        var cOut = annotated.Single(x => x.ArchetypeId == "c");
        Assert.Equal("b", cOut.NearestNeighbourId);
        Assert.Equal(9.0, cOut.NearestNeighbourDistance, precision: 6);
    }

    [Fact]
    public void Nearest_neighbour_handles_single_archetype_gracefully()
    {
        // No neighbours possible. Annotation MUST clear stale data, not crash.
        var a = Arch("a", new float[] { 0f, 0f }) with
        {
            NearestNeighbourId = "stale",
            NearestNeighbourDistance = 99,
        };
        var annotated = IdentityWeightCalibrationService
            .AnnotateNearestNeighbours(new[] { a });
        var only = Assert.Single(annotated);
        Assert.Null(only.NearestNeighbourId);
        Assert.Equal(0, only.NearestNeighbourDistance);
    }

    [Fact]
    public void Nearest_neighbour_handles_empty_input()
    {
        var annotated = IdentityWeightCalibrationService
            .AnnotateNearestNeighbours(Array.Empty<IdentityArchetype>());
        Assert.Empty(annotated);
    }

    private static IdentityArchetype Arch(string id, float[] centroid) => new()
    {
        ArchetypeId = id,
        Name = id,
        ArchetypeKind = "test",
        Centroid = centroid,
        DimensionMask = new float[centroid.Length],
    };
}
