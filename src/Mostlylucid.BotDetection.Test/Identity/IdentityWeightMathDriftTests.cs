using Mostlylucid.BotDetection.Identity;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Pure-math tests for <see cref="IdentityWeightMath.TopDriftSlot"/>. The contract:
///     find the named layout slot with the largest per-slot weighted L2 distance, normalised
///     by slot width so wide LSH slots don't auto-win on dimension count alone.
/// </summary>
public class IdentityWeightMathDriftTests
{
    private static readonly IdentityVectorLayout Layout = IdentityVectorLayout.DefaultV1();

    private static float[] UniformWeights() => Enumerable.Repeat(1.0f, Layout.Dimension).ToArray();

    [Fact]
    public void TopDriftSlot_ReturnsSlotWithLargestScaledDistance()
    {
        // Inject a unit deviation in "network.country" only; expect that slot to win.
        var centroid = new float[Layout.Dimension];
        var observed = new float[Layout.Dimension];
        var weights = UniformWeights();

        var slot = Layout.FindSlot("network.country")!;
        for (var i = slot.Offset; i < slot.Offset + slot.Width; i++)
            observed[i] = 1.0f;

        var result = IdentityWeightMath.TopDriftSlot(observed, centroid, weights, Layout);

        Assert.NotNull(result);
        Assert.Equal("network.country", result!.Value.SlotName);
        Assert.Equal("network", result.Value.Category);
        Assert.True(result.Value.Score > 0);
    }

    [Fact]
    public void TopDriftSlot_ReturnsNull_WhenVectorsIdentical()
    {
        var v = new float[Layout.Dimension];
        var w = UniformWeights();

        Assert.Null(IdentityWeightMath.TopDriftSlot(v, v, w, Layout));
    }

    [Fact]
    public void TopDriftSlot_ReturnsNull_WhenLengthsMismatch()
    {
        var observed = new float[Layout.Dimension];
        var centroid = new float[Layout.Dimension - 1];
        var weights = UniformWeights();

        Assert.Null(IdentityWeightMath.TopDriftSlot(observed, centroid, weights, Layout));
    }

    [Fact]
    public void TopDriftSlot_WidthNormalised_NarrowSlotCanWin_AgainstEqualPerDimDeviationInWideSlot()
    {
        // Pre-calibration: weights are uniform. A wide slot (4 dims) and a narrow slot (1 dim)
        // each have one unit of per-dim deviation. Without normalisation the wide slot wins
        // 4 to 1 purely on width. With width normalisation the per-dim averages are equal,
        // and a slight nudge on the narrow slot tips it.
        var centroid = new float[Layout.Dimension];
        var observed = new float[Layout.Dimension];
        var weights = UniformWeights();

        var narrowSlot = Layout.FindSlot("hdr.upgrade_insecure_requests")!; // width 1
        var wideSlot = Layout.FindSlot("hdr.accept")!;                       // width 4
        Assert.Equal(1, narrowSlot.Width);
        Assert.Equal(4, wideSlot.Width);

        // Narrow slot deviation: 1.1; wide slot per-dim deviation: 1.0
        for (var i = narrowSlot.Offset; i < narrowSlot.Offset + narrowSlot.Width; i++)
            observed[i] = 1.1f;
        for (var i = wideSlot.Offset; i < wideSlot.Offset + wideSlot.Width; i++)
            observed[i] = 1.0f;

        var result = IdentityWeightMath.TopDriftSlot(observed, centroid, weights, Layout);

        Assert.NotNull(result);
        Assert.Equal("hdr.upgrade_insecure_requests", result!.Value.SlotName);
    }

    [Fact]
    public void TopDriftSlot_RespectsWeights()
    {
        // Two slots have the same per-dim deviation; boost weights on slot B so it wins.
        var centroid = new float[Layout.Dimension];
        var observed = new float[Layout.Dimension];
        var weights = UniformWeights();

        var slotA = Layout.FindSlot("network.country")!;
        var slotB = Layout.FindSlot("network.asn")!;

        for (var i = slotA.Offset; i < slotA.Offset + slotA.Width; i++) observed[i] = 1.0f;
        for (var i = slotB.Offset; i < slotB.Offset + slotB.Width; i++) observed[i] = 1.0f;

        for (var i = slotB.Offset; i < slotB.Offset + slotB.Width; i++) weights[i] = 10.0f;

        var result = IdentityWeightMath.TopDriftSlot(observed, centroid, weights, Layout);

        Assert.NotNull(result);
        Assert.Equal("network.asn", result!.Value.SlotName);
    }

    [Fact]
    public void TopDriftSlot_CategoryIsPrefixBeforeFirstDot()
    {
        var centroid = new float[Layout.Dimension];
        var observed = new float[Layout.Dimension];
        var weights = UniformWeights();

        var slot = Layout.FindSlot("locale.accept_language_primary")!;
        for (var i = slot.Offset; i < slot.Offset + slot.Width; i++)
            observed[i] = 1.0f;

        var result = IdentityWeightMath.TopDriftSlot(observed, centroid, weights, Layout);

        Assert.NotNull(result);
        Assert.Equal("locale", result!.Value.Category);
    }
}
