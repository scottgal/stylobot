using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services.Llm;

namespace Mostlylucid.BotDetection.Test.Services.Llm;

/// <summary>
///     EC6e tests pinning the contract of the picker that replaced the queue-based
///     SignatureDescriptionService:
///       1. Pick excludes keys already reserved in the in-flight set.
///       2. Pick skips signatures below the request-count threshold and surfaces
///          them once the threshold is crossed.
/// </summary>
public class DriftTriggeredSignaturePickerTests
{
    private static readonly IReadOnlyDictionary<string, object?> EmptySignals =
        new Dictionary<string, object?>();

    private static IOptions<BotDetectionOptions> OptionsWithThreshold(int threshold)
    {
        var opts = new BotDetectionOptions { SignatureDescriptionThreshold = threshold };
        return Options.Create(opts);
    }

    [Fact]
    public void Pick_excludes_keys_already_in_flight()
    {
        var inFlight = new SignatureInFlightSet();
        var picker = new DriftTriggeredSignaturePicker(inFlight, OptionsWithThreshold(1));

        picker.TrackSignature("sig-A", EmptySignals);
        picker.TrackSignature("sig-B", EmptySignals);

        // Pre-reserve sig-A so the picker should skip it.
        Assert.True(inFlight.TryReserve("sig-A"));

        var picked = picker.Pick(maxCount: 10);

        Assert.DoesNotContain(picked, p => p.Signature == "sig-A");
        Assert.Contains(picked, p => p.Signature == "sig-B");
    }

    [Fact]
    public void Pick_skips_items_below_threshold_then_surfaces_after_cross()
    {
        var inFlight = new SignatureInFlightSet();
        // Threshold = 3 so a single TrackSignature doesn't qualify.
        var picker = new DriftTriggeredSignaturePicker(inFlight, OptionsWithThreshold(3));

        picker.TrackSignature("sig-X", EmptySignals); // count = 1

        var pickedBefore = picker.Pick(maxCount: 10);
        Assert.Empty(pickedBefore);

        // Two more pushes drives the count to 3 and the threshold is crossed.
        picker.TrackSignature("sig-X", EmptySignals);
        picker.TrackSignature("sig-X", EmptySignals);

        var pickedAfter = picker.Pick(maxCount: 10);
        Assert.Contains(pickedAfter, p => p.Signature == "sig-X");
    }
}
