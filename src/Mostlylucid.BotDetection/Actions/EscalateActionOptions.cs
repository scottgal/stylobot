using Mostlylucid.BotDetection.Events;

namespace Mostlylucid.BotDetection.Actions;

/// <summary>
///     Configuration for <see cref="EscalateToLearningActionPolicy"/>.
///     Escalators are side-effect action policies -- they hand the visit off
///     to the learning fabric (typed signal on
///     <see cref="Mostlylucid.BotDetection.Learning.ILearningCoordinator.Signals"/>)
///     and let the request continue unchanged. Threshold gates prevent the
///     hot path from spamming the fabric on every request.
/// </summary>
public sealed class EscalateToLearningActionOptions
{
    /// <summary>
    ///     Which <see cref="LearningEventType"/> to raise. Defaults to
    ///     <see cref="LearningEventType.HighConfidenceDetection"/> because the
    ///     escalator is typically dispatched off a strong-verdict rule; YAML
    ///     rules that fire on uncertain evidence should override to
    ///     <see cref="LearningEventType.InferenceRequest"/>.
    /// </summary>
    public LearningEventType EventType { get; set; } = LearningEventType.HighConfidenceDetection;

    /// <summary>
    ///     Minimum bot probability required to raise. Under this the policy
    ///     no-ops and returns <c>Allowed</c>. Bounds the escalation rate on
    ///     low-signal traffic.
    /// </summary>
    public double MinBotProbability { get; set; } = 0.0;

    /// <summary>
    ///     Minimum confidence required to raise. Same rationale as
    ///     <see cref="MinBotProbability"/> -- escalating on low-confidence
    ///     detections poisons the learning fabric with noisy labels.
    /// </summary>
    public double MinConfidence { get; set; } = 0.0;

    /// <summary>
    ///     When <c>true</c>, includes the aggregated evidence signals as the
    ///     raised <see cref="LearningEvent.Features"/> map. Off by default
    ///     because it inflates event payload size; turn on when the operator
    ///     needs feature-vector-level training data.
    /// </summary>
    public bool IncludeFeatureVector { get; set; } = false;
}

/// <summary>
///     Configuration for <see cref="EscalateToSessionActionPolicy"/>.
///     Dispatches to the per-session coordinator via the cache. No knobs
///     today -- the cache options (capacity, TTL, per-session sink cap) are
///     wired separately.
/// </summary>
public sealed class EscalateToSessionActionOptions
{
    /// <summary>
    ///     Minimum bot probability required to escalate to the session
    ///     coordinator. Sessions still receive operation completions on
    ///     every request (that's how the window fills); this threshold gates
    ///     ONLY the explicit escalation, so leave at 0.0 unless the operator
    ///     wants to filter noise.
    /// </summary>
    public double MinBotProbability { get; set; } = 0.0;
}

/// <summary>
///     Configuration for <see cref="EscalateToLlmActionPolicy"/>. Enqueues
///     the request onto <see cref="Mostlylucid.BotDetection.Services.LlmClassificationCoordinator"/>
///     for out-of-band classification. When no LLM provider is configured
///     the coordinator no-ops on drain.
/// </summary>
public sealed class EscalateToLlmActionOptions
{
    /// <summary>
    ///     Reason string stamped on the enqueue request. Surfaces in coordinator
    ///     stats and metrics; helpful for debugging which rule triggered which
    ///     LLM call.
    /// </summary>
    public string EnqueueReason { get; set; } = "escalate";

    /// <summary>
    ///     Minimum bot probability required to enqueue. LLM calls are the
    ///     most expensive escalation lane -- default to escalate only when
    ///     the heuristic is genuinely uncertain (avoid burning tokens on
    ///     obvious verdicts).
    /// </summary>
    public double MinBotProbability { get; set; } = 0.15;

    /// <summary>
    ///     Upper bot-probability bound. Together with
    ///     <see cref="MinBotProbability"/> forms the "uncertain band" that
    ///     benefits most from LLM classification.
    /// </summary>
    public double MaxBotProbability { get; set; } = 0.85;

    /// <summary>
    ///     Mark enqueued requests as drift samples so the coordinator's
    ///     drift-detection path fires. Turn off when the operator only wants
    ///     ad-hoc classification without drift comparison.
    /// </summary>
    public bool IsDriftSample { get; set; } = true;
}