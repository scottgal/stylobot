using Mostlylucid.BotDetection.Events;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Learning;

/// <summary>
///     Typed signal keys published on <see cref="LearningCoordinator.Signals"/>.
///     One key per <see cref="LearningEventType"/> so consumers subscribe only
///     to the categories they care about — "many small signals" rather than
///     one blob-key everyone filters. Producers pick the key via
///     <see cref="For"/>; the sink transports the strongly-typed
///     <see cref="LearningEvent"/> payload with zero serialization.
/// </summary>
public static class LearningSignalKeys
{
    public static readonly SignalKey<LearningEvent> HighConfidenceDetection = new("learning.detection.high_confidence");
    public static readonly SignalKey<LearningEvent> MinimalDetection = new("learning.detection.minimal");
    public static readonly SignalKey<LearningEvent> FullAnalysisRequest = new("learning.detection.full_analysis_request");
    public static readonly SignalKey<LearningEvent> FullDetection = new("learning.detection.full");
    public static readonly SignalKey<LearningEvent> PatternDiscovered = new("learning.pattern.discovered");
    public static readonly SignalKey<LearningEvent> InconsistencyDetected = new("learning.pattern.inconsistency");
    public static readonly SignalKey<LearningEvent> UserFeedback = new("learning.feedback.user");
    public static readonly SignalKey<LearningEvent> ClientSideValidation = new("learning.validation.client_side");
    public static readonly SignalKey<LearningEvent> InferenceRequest = new("learning.inference.request");
    public static readonly SignalKey<LearningEvent> ModelUpdated = new("learning.model.updated");
    public static readonly SignalKey<LearningEvent> FastPathDriftDetected = new("learning.drift.fast_path");
    public static readonly SignalKey<LearningEvent> SignatureFeedback = new("learning.feedback.signature");
    public static readonly SignalKey<LearningEvent> FastPathRuleUpdate = new("learning.rule.fast_path_update");
    public static readonly SignalKey<LearningEvent> IntentClassified = new("learning.intent.classified");

    /// <summary>
    ///     Map a <see cref="LearningEventType"/> to the matching typed key.
    /// </summary>
    public static SignalKey<LearningEvent> For(LearningEventType type) => type switch
    {
        LearningEventType.HighConfidenceDetection => HighConfidenceDetection,
        LearningEventType.MinimalDetection => MinimalDetection,
        LearningEventType.FullAnalysisRequest => FullAnalysisRequest,
        LearningEventType.FullDetection => FullDetection,
        LearningEventType.PatternDiscovered => PatternDiscovered,
        LearningEventType.InconsistencyDetected => InconsistencyDetected,
        LearningEventType.UserFeedback => UserFeedback,
        LearningEventType.ClientSideValidation => ClientSideValidation,
        LearningEventType.InferenceRequest => InferenceRequest,
        LearningEventType.ModelUpdated => ModelUpdated,
        LearningEventType.FastPathDriftDetected => FastPathDriftDetected,
        LearningEventType.SignatureFeedback => SignatureFeedback,
        LearningEventType.FastPathRuleUpdate => FastPathRuleUpdate,
        LearningEventType.IntentClassified => IntentClassified,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown LearningEventType")
    };
}