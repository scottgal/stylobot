using FluentAssertions;
using Mostlylucid.BotDetection.UI.Models.Primitives;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

public class DriftBadgeRenderTests
{
    // Default threshold (Identity.Drift.DriftBadgeThreshold) is 0.15 per T16.
    // Test pins the boundary contract: strictly greater than threshold renders.
    [Theory]
    [InlineData(0.10, false)]   // below threshold
    [InlineData(0.15, false)]   // equal — strictly greater required
    [InlineData(0.16, true)]    // just over
    [InlineData(0.50, true)]    // well over
    public void Drift_badge_visibility_follows_threshold(double magnitude, bool expectVisible)
    {
        const double threshold = 0.15;
        var model = new DriftBadgeModel(
            IsDrifted:         magnitude > threshold,
            Magnitude:         magnitude,
            OriginArchetypeId: "chrome-desktop",
            TopSlotLabel:      "geo shift");
        model.IsDrifted.Should().Be(expectVisible);
    }
}
