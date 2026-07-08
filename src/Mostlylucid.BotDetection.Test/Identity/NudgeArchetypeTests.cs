using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Identity;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Unit tests for <see cref="IdentityArchetypeRegistry.NudgeArchetype"/>.
///     Covers the two hard constraints ratified by overview:
///     fail-closed (unknown id = silent no-op, never creates an archetype lazily)
///     and no-clobber (centroid moves toward vector by bounded EMA, never hard-replaced).
/// </summary>
public sealed class NudgeArchetypeTests
{
    private static readonly int Dim = IdentityVectorLayout.DefaultV1().Dimension;

    private static IdentityArchetypeRegistry NewRegistry(params IdentityArchetype[] archetypes)
    {
        var registry = new IdentityArchetypeRegistry(
            NullLogger<IdentityArchetypeRegistry>.Instance,
            new IdentityVectorEncoder(IdentityVectorLayout.DefaultV1()));
        if (archetypes.Length > 0)
            registry.Replace(archetypes);
        return registry;
    }

    private static IdentityArchetype MakeArchetype(string id, float centroidValue = 0f)
    {
        var centroid = new float[Dim];
        Array.Fill(centroid, centroidValue);
        var mask = new float[Dim];
        return new IdentityArchetype
        {
            ArchetypeId = id,
            Name = id,
            ArchetypeKind = "test",
            Centroid = centroid,
            DimensionMask = mask,
        };
    }

    private static float[] AllOnes() { var v = new float[Dim]; Array.Fill(v, 1f); return v; }

    // ── happy path: centroid shifts toward vector by bounded EMA ────────────

    [Fact]
    public void NudgeArchetype_moves_centroid_toward_vector_and_does_not_hard_replace_it()
    {
        var registry = NewRegistry(MakeArchetype("verified-gptbot", centroidValue: 0f));
        var target = AllOnes();

        registry.NudgeArchetype("verified-gptbot", target.AsMemory());

        var after = registry.TryGetById("verified-gptbot")!;
        // Centroid must have moved (0 -> partial toward 1)
        after.Centroid.Should().NotEqual(new float[Dim], "centroid must shift from the all-zero start");
        // No-clobber: centroid must NOT equal the raw input vector
        after.Centroid.Should().NotEqual(target, "centroid must be bounded EMA, not hard-replaced by the vector");
        // Every dim must be strictly between 0 and 1 (EMA with weight 0.05)
        after.Centroid.All(v => v > 0f && v < 1f).Should().BeTrue(
            "each dimension should be strictly between zero (start) and one (target) after a single nudge");
    }

    [Fact]
    public void NudgeArchetype_default_weight_yields_correct_ema_step()
    {
        const float centroidStart = 0.0f;
        const float vectorVal = 1.0f;
        const double defaultWeight = 0.05;

        var registry = NewRegistry(MakeArchetype("bot-a", centroidValue: centroidStart));
        var vec = new float[Dim];
        Array.Fill(vec, vectorVal);

        registry.NudgeArchetype("bot-a", vec.AsMemory());

        var expected = (float)(centroidStart * (1.0 - defaultWeight) + vectorVal * defaultWeight);
        var actual = registry.TryGetById("bot-a")!.Centroid[0];
        actual.Should().BeApproximately(expected, precision: 1e-6f, "EMA formula: c = c*(1-w) + v*w");
    }

    [Fact]
    public void NudgeArchetype_custom_weight_is_applied()
    {
        const double customWeight = 0.2;
        const float start = 0f;
        const float target = 1f;

        var registry = NewRegistry(MakeArchetype("bot-b", centroidValue: start));
        var vec = new float[Dim];
        Array.Fill(vec, target);

        registry.NudgeArchetype("bot-b", vec.AsMemory(), weight: customWeight);

        var expected = (float)(start * (1.0 - customWeight) + target * customWeight);
        registry.TryGetById("bot-b")!.Centroid[0]
            .Should().BeApproximately(expected, precision: 1e-6f);
    }

    [Fact]
    public void NudgeArchetype_weight_is_clamped_above_half_to_prevent_large_moves()
    {
        // A weight > 0.5 should be clamped to 0.5, ensuring no-clobber even
        // if the caller passes an unreasonable weight.
        var registry = NewRegistry(MakeArchetype("bot-c", centroidValue: 0f));
        var vec = new float[Dim];
        Array.Fill(vec, 1f);

        registry.NudgeArchetype("bot-c", vec.AsMemory(), weight: 0.99);

        var afterClamped = registry.TryGetById("bot-c")!.Centroid[0];
        // With clamp to 0.5: expected = 0 * 0.5 + 1 * 0.5 = 0.5
        afterClamped.Should().BeApproximately(0.5f, precision: 1e-6f,
            "weight capped at 0.5 means EMA can move at most halfway in one step");
        // Most importantly: the centroid is NOT equal to 1.0 (the raw vector)
        afterClamped.Should().BeLessThan(1f, "clamped weight must prevent hard-replace");
    }

    // ── fail-closed: unknown archetype id → silent no-op ───────────────────

    [Fact]
    public void NudgeArchetype_unknown_id_is_silent_noop_no_archetype_created()
    {
        var registry = NewRegistry(MakeArchetype("existing-bot"));
        var countBefore = registry.All.Count;
        var centroidBefore = (float[])registry.TryGetById("existing-bot")!.Centroid.Clone();

        registry.NudgeArchetype("does-not-exist", AllOnes().AsMemory());

        // No new archetype created
        registry.All.Count.Should().Be(countBefore, "fail-closed: unknown id must not create a new archetype");
        // Existing archetype untouched
        registry.TryGetById("existing-bot")!.Centroid.Should().Equal(centroidBefore,
            "existing archetypes must not be affected when nudging an unknown id");
        // Still does not exist
        registry.TryGetById("does-not-exist").Should().BeNull();
    }

    [Fact]
    public void NudgeArchetype_null_or_empty_id_is_silent_noop()
    {
        var registry = NewRegistry(MakeArchetype("existing-bot"));
        var countBefore = registry.All.Count;

        registry.NudgeArchetype(string.Empty, AllOnes().AsMemory());
        registry.All.Count.Should().Be(countBefore);
    }

    // ── dimension mismatch → silent no-op ───────────────────────────────────

    [Fact]
    public void NudgeArchetype_dimension_mismatch_is_silent_noop()
    {
        var registry = NewRegistry(MakeArchetype("bot-d", centroidValue: 0f));
        var centroidBefore = (float[])registry.TryGetById("bot-d")!.Centroid.Clone();

        // Vector with wrong dimension (too short)
        var wrongDim = new float[Dim - 1];
        Array.Fill(wrongDim, 1f);

        registry.NudgeArchetype("bot-d", wrongDim.AsMemory());

        registry.TryGetById("bot-d")!.Centroid.Should().Equal(centroidBefore,
            "dimension mismatch must leave the centroid unchanged");
    }

    // ── case-insensitive id lookup ───────────────────────────────────────────

    [Fact]
    public void NudgeArchetype_id_lookup_is_case_insensitive()
    {
        var registry = NewRegistry(MakeArchetype("verified-gptbot", centroidValue: 0f));
        var vec = AllOnes();

        // Pass id in different case
        registry.NudgeArchetype("VERIFIED-GPTBOT", vec.AsMemory());

        var after = registry.TryGetById("verified-gptbot")!.Centroid[0];
        after.Should().BeGreaterThan(0f, "case-insensitive match must still result in a nudge");
    }
}