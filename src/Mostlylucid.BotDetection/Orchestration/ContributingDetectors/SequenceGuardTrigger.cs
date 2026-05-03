using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Shared trigger guard used by deferred detectors to skip when the content sequence
///     is active and the fingerprint is still on-track early in the sequence (positions 0-2).
///     Detectors run when: no sequence active, sequence diverged, or position >= 3.
/// </summary>
internal static class SequenceGuardTrigger
{
    internal const int MinPosition = 3;

    /// <summary>
    ///     AnyOf: run if no sequence position signal exists, on-track is false,
    ///     diverged is true, OR position has reached the minimum threshold.
    /// </summary>
    internal static readonly AnyOfTrigger Default = new([
        new SignalNotExistsTrigger(SignalKeys.SequencePosition),
        new SignalValueTrigger<bool>(SignalKeys.SequenceOnTrack, false),
        new SignalValueTrigger<bool>(SignalKeys.SequenceDiverged, true),
        new SignalPredicateTrigger<int>(SignalKeys.SequencePosition, p => p >= MinPosition, $"position >= {MinPosition}")
    ]);
}
