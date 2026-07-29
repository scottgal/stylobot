using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Supplies the canonical classification thresholds (<c>BotDetection:Classification</c>:
///     BotFloor / HumanCeiling / MinActionConfidence) to the dashboard "Apply-policy" control,
///     abstracting over WHERE the thresholds are read.
///
///     <para>
///     Mirrors <see cref="IConfigBaselineProvider"/>: a LOCAL-detection dashboard reads the numbers
///     from in-process <c>IOptions&lt;BotDetectionOptions&gt;</c>; a REMOTE / thin-client dashboard
///     (whose own config is empty) reads the GATEWAY's thresholds over the REST read surface via a
///     remote implementation. Read-only in both modes — there is NO write path here. <c>canEdit</c>
///     rides the same <c>IPolicyCanEditPolicy.CanEdit</c> gate as the policy rows (FOSS read-only).
///     </para>
/// </summary>
public interface IClassificationThresholdProvider
{
    /// <summary>
    ///     The canonical classification thresholds as a single read-only row. Async because a remote
    ///     implementation fetches over HTTP; the local implementation completes synchronously.
    /// </summary>
    Task<ClassificationThresholdRow> GetThresholdsAsync(bool canEdit, CancellationToken ct = default);
}
