using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Identity;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

/// <summary>
///     The Phase 3 killer property: VarianceMultiplier below 1.0 actually
///     narrows the matcher's catchment. Proves the wiring through the matcher
///     -- shrinking the multiplier in the calibration pass would be useless
///     observability theatre if the scorer didn't honour it.
///     <para>
///         Strategy: build two archetypes with overlapping coverage. Score the
///         same observation against each. Pre-shrinkage, the umbrella wins.
///         Post-shrinkage, the same observation scores LOWER on the umbrella
///         (or the umbrella loses outright to its neighbour). Pin both shapes.
///     </para>
/// </summary>
public sealed class UmbrellaShrinkageMatcherEffectTests
{
    [Fact]
    public void Lowering_variance_multiplier_reduces_the_score_for_distant_observations()
    {
        // Pin the load-bearing matcher contract: a tight archetype penalises
        // deviation MORE than a loose one. With the observation BETWEEN the
        // centroid and the edge of the catchment, lowering VarianceMultiplier
        // MUST drop the score -- that's exactly the umbrella-narrowing effect
        // calibration relies on. (Note: at the centroid itself the Gaussian
        // specificity-reward term goes the other way -- tighter variance
        // scores HIGHER on an exact match. That's the matcher's "tight
        // archetypes are preferred for near-centroid observations" contract
        // and is correct; the umbrella-narrowing effect only shows for
        // observations away from the centroid, which is where leakage lives.)
        var encoder = new IdentityVectorEncoder(IdentityVectorLayout.DefaultV1());
        var registry = new IdentityArchetypeRegistry(
            NullLogger<IdentityArchetypeRegistry>.Instance, encoder);

        Assert.True(registry.All.Count >= 1,
            "test needs at least one real archetype to score against");
        var sample = registry.All[0];

        // Build a distant-but-not-orthogonal observation: take the centroid
        // and push every populated dim by +0.3. This is the "leakage shape"
        // -- close enough to compete, far enough that tightening will bite.
        var observation = (float[])sample.Centroid.Clone();
        for (var i = 0; i < observation.Length; i++)
            if (Math.Abs(observation[i]) > 1e-6f)
                observation[i] += 0.3f;

        var preShrinkScore = registry.ScoreAgainst(observation, sample);

        // Tighten the catchment. ScoreAgainst takes any IdentityArchetype, so
        // we don't need to mutate the registry.
        var tightened = sample with { VarianceMultiplier = 0.2 };
        var postShrinkScore = registry.ScoreAgainst(observation, tightened);

        Assert.NotNull(preShrinkScore);
        Assert.NotNull(postShrinkScore);
        Assert.True(postShrinkScore < preShrinkScore,
            $"post-shrink score {postShrinkScore:F4} must be < pre-shrink score {preShrinkScore:F4} for distant observation");
    }

    [Fact]
    public void Centroid_match_score_does_not_decrease_under_shrinkage()
    {
        // Companion to the test above: for observations at the centroid,
        // shrinkage MUST NOT make the score worse. The Gaussian
        // specificity-reward (-0.5 * log(v)) grows as variance shrinks, so
        // an exact match scores at-least-as-high after shrinkage. Pin so a
        // future refactor doesn't accidentally apply the multiplier as a
        // straight score scalar.
        var encoder = new IdentityVectorEncoder(IdentityVectorLayout.DefaultV1());
        var registry = new IdentityArchetypeRegistry(
            NullLogger<IdentityArchetypeRegistry>.Instance, encoder);
        var sample = registry.All[0];

        var observation = (float[])sample.Centroid.Clone();
        var preScore = registry.ScoreAgainst(observation, sample);
        var tightened = sample with { VarianceMultiplier = 0.5 };
        var postScore = registry.ScoreAgainst(observation, tightened);

        Assert.NotNull(preScore);
        Assert.NotNull(postScore);
        Assert.True(postScore >= preScore - 1e-6,
            $"centroid-match score regressed under shrinkage: {preScore} -> {postScore}");
    }

    [Fact]
    public void Variance_multiplier_clamps_below_floor_so_matcher_stays_finite()
    {
        // Defensive: a maliciously-or-buggily-set VarianceMultiplier below the
        // 0.05 floor MUST not produce NaN / Inf in the matcher's Gaussian
        // log-likelihood. The matcher clamps internally; pin that.
        var encoder = new IdentityVectorEncoder(IdentityVectorLayout.DefaultV1());
        var registry = new IdentityArchetypeRegistry(
            NullLogger<IdentityArchetypeRegistry>.Instance, encoder);
        var first = registry.All[0];

        var observation = (float[])first.Centroid.Clone();

        var pathological = first with { VarianceMultiplier = 0.0 };  // would div-by-zero without clamp
        var score = registry.ScoreAgainst(observation, pathological);
        Assert.NotNull(score);
        Assert.True(!double.IsNaN(score!.Value) && !double.IsInfinity(score.Value),
            $"matcher produced non-finite score {score} under zero VarianceMultiplier");
    }
}
