using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Persists visitor "flag as wrong" feedback. Lives on the enforcement component
///     (gateway) alongside the rest of the dashboard state — the upstream site POSTs
///     to <c>/api/v1/feedback</c> rather than writing a store itself
///     (feedback_upstream_owns_no_stylobot_state). SQLite by default; the commercial
///     PostgreSQL pack replaces it on the gateway.
/// </summary>
public interface IDetectionFeedbackStore
{
    /// <summary>Record one flag. Returns false on a soft failure (logged, never throws).</summary>
    Task<bool> RecordFlagAsync(DetectionFeedbackRecord feedback, CancellationToken ct = default);
}