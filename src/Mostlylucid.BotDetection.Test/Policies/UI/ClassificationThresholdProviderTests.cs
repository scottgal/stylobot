using FluentAssertions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Policies.UI;

/// <summary>
///     Pins <see cref="LocalClassificationThresholdProvider"/> -- the FOSS read accessor the dashboard
///     "Apply-policy" control uses to surface the canonical classification thresholds
///     (<c>BotDetection:Classification</c>: BotFloor / HumanCeiling / MinActionConfidence) in plain
///     language. The provider is READ-ONLY: it projects <see cref="ClassificationOptions"/> straight
///     through and never mutates or enforces. Enforcement of the <c>MinActionConfidence</c> gate is a
///     deliberate fast-follow and is out of scope here.
/// </summary>
public sealed class ClassificationThresholdProviderTests
{
    private static LocalClassificationThresholdProvider NewProvider(BotDetectionOptions opts)
        => new(Options.Create(opts));

    [Fact]
    public async Task Returns_the_configured_classification_thresholds()
    {
        var opts = new BotDetectionOptions
        {
            Classification = new ClassificationOptions
            {
                BotFloor = 0.72,
                HumanCeiling = 0.28,
                MinActionConfidence = 0.5
            }
        };

        var row = await NewProvider(opts).GetThresholdsAsync(canEdit: false);

        row.BotFloor.Should().Be(0.72);
        row.HumanCeiling.Should().Be(0.28);
        row.MinActionConfidence.Should().Be(0.5);
    }

    [Fact]
    public async Task Surfaces_the_exact_configuration_keys_the_edit_control_targets()
    {
        var row = await NewProvider(new BotDetectionOptions()).GetThresholdsAsync(canEdit: false);

        row.BotFloorConfigKey.Should().Be("BotDetection:Classification:BotFloor");
        row.HumanCeilingConfigKey.Should().Be("BotDetection:Classification:HumanCeiling");
        row.MinActionConfidenceConfigKey.Should().Be("BotDetection:Classification:MinActionConfidence");
    }

    [Fact]
    public async Task Defaults_flow_through_when_options_are_untouched()
    {
        // The single source of truth for the defaults is ClassificationOptions itself.
        var defaults = new ClassificationOptions();

        var row = await NewProvider(new BotDetectionOptions()).GetThresholdsAsync(canEdit: false);

        row.BotFloor.Should().Be(defaults.BotFloor);
        row.HumanCeiling.Should().Be(defaults.HumanCeiling);
        row.MinActionConfidence.Should().Be(defaults.MinActionConfidence);
    }

    [Fact]
    public async Task Non_default_MinActionConfidence_flows_through()
    {
        // A configured (non-default) confidence gate must reach the read surface verbatim, so the
        // dashboard control reflects the operator's setting rather than the baked-in default.
        var opts = new BotDetectionOptions
        {
            Classification = new ClassificationOptions { MinActionConfidence = 0.83 }
        };

        var row = await NewProvider(opts).GetThresholdsAsync(canEdit: false);

        row.MinActionConfidence.Should().Be(0.83)
            .And.NotBe(new ClassificationOptions().MinActionConfidence, "the operator override, not the default, must surface");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CanEdit_passes_through_unchanged(bool canEdit)
    {
        var row = await NewProvider(new BotDetectionOptions()).GetThresholdsAsync(canEdit);

        row.CanEdit.Should().Be(canEdit);
    }
}
