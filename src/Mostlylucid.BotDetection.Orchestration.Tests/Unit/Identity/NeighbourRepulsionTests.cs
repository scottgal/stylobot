using Mostlylucid.BotDetection.Identity;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

/// <summary>
///     Phase 5 v2 (2026-06-21) spec §2.D rule 2: neighbour-aware repulsion.
///     The smaller archetype's refinement target gets pushed away from its
///     nearest neighbour when the two centroids sit close enough. Pin the
///     conditions and the shape of the resulting vector without running
///     the full calibration loop.
/// </summary>
public sealed class NeighbourRepulsionTests
{
    [Fact]
    public void No_repulsion_when_strength_is_zero()
    {
        // Pin: setting strength to 0 disables repulsion (back to observe-only).
        // Defaults in production may set this to 0 to roll out cautiously.
        var dir = IdentityWeightMath.BuildRepulsionVector(
            thisCentroid: new float[] { 0f, 0f },
            neighbourCentroid: new float[] { 0.1f, 0f },
            thisDescendantCount: 1,
            neighbourDescendantCount: 100,
            currentDistance: 0.1,
            repulsionRadius: 0.5,
            repulsionStrength: 0);
        Assert.Null(dir);
    }

    [Fact]
    public void No_repulsion_when_neighbour_is_null()
    {
        // Pin: first-cycle archetypes have no nearest-neighbour annotation.
        var dir = IdentityWeightMath.BuildRepulsionVector(
            thisCentroid: new float[] { 0f, 0f },
            neighbourCentroid: null,
            thisDescendantCount: 1,
            neighbourDescendantCount: 100,
            currentDistance: 0.1,
            repulsionRadius: 0.5,
            repulsionStrength: 0.05);
        Assert.Null(dir);
    }

    [Fact]
    public void No_repulsion_when_distance_exceeds_radius()
    {
        // Pin: outside the repulsion radius the neighbour isn't close enough
        // to threaten convergence. Only firing inside the radius keeps the
        // shift localised.
        var dir = IdentityWeightMath.BuildRepulsionVector(
            thisCentroid: new float[] { 0f, 0f },
            neighbourCentroid: new float[] { 0.6f, 0f },
            thisDescendantCount: 1,
            neighbourDescendantCount: 100,
            currentDistance: 0.6,
            repulsionRadius: 0.5,
            repulsionStrength: 0.05);
        Assert.Null(dir);
    }

    [Fact]
    public void Larger_archetype_does_not_repel_from_smaller_neighbour()
    {
        // The "smaller archetype repels" rule pinned: equal-or-greater
        // descendant count = nobody moves. Prevents the both-push oscillation
        // failure mode.
        var dirLargerIsCaller = IdentityWeightMath.BuildRepulsionVector(
            thisCentroid: new float[] { 0f, 0f },
            neighbourCentroid: new float[] { 0.1f, 0f },
            thisDescendantCount: 100,
            neighbourDescendantCount: 50,
            currentDistance: 0.1,
            repulsionRadius: 0.5,
            repulsionStrength: 0.05);
        Assert.Null(dirLargerIsCaller);

        // Equal counts also stay still (deterministic break).
        var dirEqual = IdentityWeightMath.BuildRepulsionVector(
            thisCentroid: new float[] { 0f, 0f },
            neighbourCentroid: new float[] { 0.1f, 0f },
            thisDescendantCount: 50,
            neighbourDescendantCount: 50,
            currentDistance: 0.1,
            repulsionRadius: 0.5,
            repulsionStrength: 0.05);
        Assert.Null(dirEqual);
    }

    [Fact]
    public void Repulsion_pushes_caller_away_from_neighbour_with_strength_magnitude()
    {
        // Pin: direction = (this - neighbour) normalised; magnitude = strength.
        var dir = IdentityWeightMath.BuildRepulsionVector(
            thisCentroid: new float[] { 1f, 0f },
            neighbourCentroid: new float[] { 0f, 0f },
            thisDescendantCount: 1,
            neighbourDescendantCount: 100,
            currentDistance: 1.0,
            repulsionRadius: 1.5,
            repulsionStrength: 0.05);

        Assert.NotNull(dir);
        Assert.Equal(0.05f, dir![0], precision: 6);  // unit vector (1,0) * 0.05
        Assert.Equal(0f, dir[1], precision: 6);
    }

    [Fact]
    public void Repulsion_handles_coincident_centroids_without_dividing_by_zero()
    {
        // Defensive: identical centroids = no direction to push along. Caller
        // sees null and skips the repulsion term; refinement still proceeds.
        var dir = IdentityWeightMath.BuildRepulsionVector(
            thisCentroid: new float[] { 0f, 0f },
            neighbourCentroid: new float[] { 0f, 0f },
            thisDescendantCount: 1,
            neighbourDescendantCount: 100,
            currentDistance: 0,
            repulsionRadius: 0.5,
            repulsionStrength: 0.05);
        Assert.Null(dir);
    }

    [Fact]
    public void Refinement_with_repulsion_moves_the_centroid_in_the_repulsion_direction()
    {
        // Pin the end-to-end shape: feed a repulsion vector to the adaptive
        // refinement, assert the refined centroid sits further from the
        // implied neighbour (i.e. shifted in the repulsion direction). Catches
        // a sign error or a "repulsion vector applied with wrong sign".
        var centroid = new float[] { 1f, 0f };
        var descendants = new float[][] { new float[] { 1f, 0f } };  // descendants at centroid -> mean = centroid
        var repulsion = new float[] { 0.1f, 0f };                    // push along +x

        var noRepulsion = IdentityWeightMath.RefineArchetypeCentroidAdaptive(
            centroid, descendants, alpha: 0.5);
        var withRepulsion = IdentityWeightMath.RefineArchetypeCentroidAdaptive(
            centroid, descendants, alpha: 0.5, repulsion);

        Assert.NotNull(noRepulsion);
        Assert.NotNull(withRepulsion);
        // The repulsion shifts the target by 0.1 in dim 0; with alpha=0.5 the
        // centroid moves by 0.05.
        Assert.Equal(noRepulsion.Value.Refined[0] + 0.05f, withRepulsion.Value.Refined[0], precision: 5);
    }
}
