using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Local-detection implementation of <see cref="IClassificationThresholdProvider"/>: reads the
///     canonical thresholds straight from in-process <see cref="ClassificationOptions"/>
///     (<c>BotDetection:Classification</c>). Read-only — it never mutates options and has no write
///     path. A remote / thin-client host registers a remote implementation instead.
/// </summary>
public sealed class LocalClassificationThresholdProvider : IClassificationThresholdProvider
{
    /// <summary>The <c>IConfiguration</c> key for <see cref="ClassificationOptions.BotFloor"/>.</summary>
    public const string BotFloorConfigKey = "BotDetection:Classification:BotFloor";

    /// <summary>The <c>IConfiguration</c> key for <see cref="ClassificationOptions.HumanCeiling"/>.</summary>
    public const string HumanCeilingConfigKey = "BotDetection:Classification:HumanCeiling";

    /// <summary>The <c>IConfiguration</c> key for <see cref="ClassificationOptions.MinActionConfidence"/>.</summary>
    public const string MinActionConfidenceConfigKey = "BotDetection:Classification:MinActionConfidence";

    private readonly IOptions<BotDetectionOptions> _options;

    public LocalClassificationThresholdProvider(IOptions<BotDetectionOptions> options)
        => _options = options;

    public Task<ClassificationThresholdRow> GetThresholdsAsync(bool canEdit, CancellationToken ct = default)
        => Task.FromResult(Compose(canEdit));

    /// <summary>Synchronous composition seam (the local read completes without I/O); tested directly.</summary>
    public ClassificationThresholdRow Compose(bool canEdit)
    {
        var c = _options.Value.Classification;
        return new ClassificationThresholdRow(
            BotFloor: c.BotFloor,
            HumanCeiling: c.HumanCeiling,
            MinActionConfidence: c.MinActionConfidence,
            BotFloorConfigKey: BotFloorConfigKey,
            HumanCeilingConfigKey: HumanCeilingConfigKey,
            MinActionConfidenceConfigKey: MinActionConfidenceConfigKey,
            CanEdit: canEdit);
    }
}
