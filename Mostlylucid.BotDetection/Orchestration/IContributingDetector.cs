using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration;

/// <summary>
///     A detector that emits contributions (evidence) rather than verdicts.
///     Part of the blackboard architecture - detectors contribute evidence,
///     the orchestrator aggregates into a final decision.
/// </summary>
public interface IContributingDetector
{
    /// <summary>
    ///     Unique name of this detector
    /// </summary>
    string Name { get; }

    /// <summary>
    ///     Priority determines execution order when multiple detectors can run.
    ///     Lower = runs first. Critical (0) runs before Normal (100).
    /// </summary>
    int Priority => 100;

    /// <summary>
    ///     Whether this detector is enabled.
    ///     Checked before running - allows runtime disable.
    /// </summary>
    bool IsEnabled => true;

    /// <summary>
    ///     Trigger conditions that must be met before this detector runs.
    ///     Empty = no conditions, runs in the first wave.
    /// </summary>
    IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    /// <summary>
    ///     Maximum time to wait for trigger conditions before skipping.
    ///     Default: 500ms
    /// </summary>
    TimeSpan TriggerTimeout => TimeSpan.FromMilliseconds(500);

    /// <summary>
    ///     Maximum time allowed for this detector to execute.
    ///     Default: 2 seconds
    /// </summary>
    TimeSpan ExecutionTimeout => TimeSpan.FromSeconds(2);

    /// <summary>
    ///     Whether this detector can be skipped if it times out or fails.
    ///     Default: true (most detectors are optional)
    /// </summary>
    bool IsOptional => true;

    /// <summary>
    ///     Run detection and return zero or more contributions.
    ///     Detector receives the blackboard state and can read signals from prior detectors.
    /// </summary>
    Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Condition that must be met for a detector to run.
/// </summary>
public abstract record TriggerCondition
{
    /// <summary>
    ///     Human-readable description of this condition
    /// </summary>
    public abstract string Description { get; }

    /// <summary>
    ///     Check if this condition is satisfied given the current blackboard signals.
    /// </summary>
    public abstract bool IsSatisfied(IReadOnlyDictionary<string, object> signals);
}

/// <summary>
///     Trigger when a specific signal key exists
/// </summary>
public sealed record SignalExistsTrigger(string SignalKey) : TriggerCondition
{
    public override string Description => $"Signal '{SignalKey}' exists";

    public override bool IsSatisfied(IReadOnlyDictionary<string, object> signals)
    {
        return signals.ContainsKey(SignalKey);
    }
}

/// <summary>
///     Trigger when a signal has a specific value
/// </summary>
public sealed record SignalValueTrigger<T>(string SignalKey, T ExpectedValue) : TriggerCondition
{
    public override string Description => $"Signal '{SignalKey}' == {ExpectedValue}";

    public override bool IsSatisfied(IReadOnlyDictionary<string, object> signals)
    {
        return signals.TryGetValue(SignalKey, out var value) &&
               value is T typed &&
               EqualityComparer<T>.Default.Equals(typed, ExpectedValue);
    }
}

/// <summary>
///     Trigger when a signal satisfies a predicate
/// </summary>
public sealed record SignalPredicateTrigger<T>(
    string SignalKey,
    Func<T, bool> Predicate,
    string PredicateDescription) : TriggerCondition
{
    public override string Description => $"Signal '{SignalKey}' {PredicateDescription}";

    public override bool IsSatisfied(IReadOnlyDictionary<string, object> signals)
    {
        return signals.TryGetValue(SignalKey, out var value) &&
               value is T typed &&
               Predicate(typed);
    }
}

/// <summary>
///     Trigger when any of the sub-conditions are met
/// </summary>
public sealed record AnyOfTrigger(IReadOnlyList<TriggerCondition> Conditions) : TriggerCondition
{
    public override string Description => $"Any of: [{string.Join(", ", Conditions.Select(c => c.Description))}]";

    public override bool IsSatisfied(IReadOnlyDictionary<string, object> signals)
    {
        return Conditions.Any(c => c.IsSatisfied(signals));
    }
}

/// <summary>
///     Trigger when all of the sub-conditions are met
/// </summary>
public sealed record AllOfTrigger(IReadOnlyList<TriggerCondition> Conditions) : TriggerCondition
{
    public override string Description => $"All of: [{string.Join(", ", Conditions.Select(c => c.Description))}]";

    public override bool IsSatisfied(IReadOnlyDictionary<string, object> signals)
    {
        return Conditions.All(c => c.IsSatisfied(signals));
    }
}

/// <summary>
///     Trigger when a certain number of detectors have completed
/// </summary>
public sealed record DetectorCountTrigger(int MinDetectors) : TriggerCondition
{
    public const string CompletedDetectorsSignal = "_system.completed_detectors";

    public override string Description => $"At least {MinDetectors} detectors completed";

    public override bool IsSatisfied(IReadOnlyDictionary<string, object> signals)
    {
        return signals.TryGetValue(CompletedDetectorsSignal, out var value) &&
               value is int count &&
               count >= MinDetectors;
    }
}

/// <summary>
///     Trigger when current risk score exceeds a threshold
/// </summary>
public sealed record RiskThresholdTrigger(double MinScore) : TriggerCondition
{
    public const string CurrentRiskSignal = "_system.current_risk";

    public override string Description => $"Risk score >= {MinScore:F2}";

    public override bool IsSatisfied(IReadOnlyDictionary<string, object> signals)
    {
        return signals.TryGetValue(CurrentRiskSignal, out var value) &&
               value is double score &&
               score >= MinScore;
    }
}

/// <summary>
///     Helper class for building trigger conditions fluently
/// </summary>
public static class Triggers
{
    /// <summary>
    ///     Common: trigger when IP is from datacenter
    /// </summary>
    public static TriggerCondition WhenDatacenterIp =>
        WhenSignalEquals(SignalKeys.IpIsDatacenter, true);

    /// <summary>
    ///     Common: trigger when UA looks like a bot
    /// </summary>
    public static TriggerCondition WhenUaIsBot =>
        WhenSignalEquals(SignalKeys.UserAgentIsBot, true);

    /// <summary>
    ///     Common: trigger when risk is medium or higher
    /// </summary>
    public static TriggerCondition WhenRiskMediumOrHigher =>
        WhenRiskExceeds(0.5);

    /// <summary>
    ///     Trigger when a signal exists
    /// </summary>
    public static TriggerCondition WhenSignalExists(string signalKey)
    {
        return new SignalExistsTrigger(signalKey);
    }

    /// <summary>
    ///     Trigger when a signal has a specific value
    /// </summary>
    public static TriggerCondition WhenSignalEquals<T>(string signalKey, T value)
    {
        return new SignalValueTrigger<T>(signalKey, value);
    }

    /// <summary>
    ///     Trigger when a signal satisfies a predicate
    /// </summary>
    public static TriggerCondition WhenSignal<T>(
        string signalKey,
        Func<T, bool> predicate,
        string description)
    {
        return new SignalPredicateTrigger<T>(signalKey, predicate, description);
    }

    /// <summary>
    ///     Trigger when any condition is met
    /// </summary>
    public static TriggerCondition AnyOf(params TriggerCondition[] conditions)
    {
        return new AnyOfTrigger(conditions);
    }

    /// <summary>
    ///     Trigger when all conditions are met
    /// </summary>
    public static TriggerCondition AllOf(params TriggerCondition[] conditions)
    {
        return new AllOfTrigger(conditions);
    }

    /// <summary>
    ///     Trigger when enough detectors have completed
    /// </summary>
    public static TriggerCondition WhenDetectorCount(int min)
    {
        return new DetectorCountTrigger(min);
    }

    /// <summary>
    ///     Trigger when risk exceeds threshold
    /// </summary>
    public static TriggerCondition WhenRiskExceeds(double threshold)
    {
        return new RiskThresholdTrigger(threshold);
    }
}

/// <summary>
///     Immutable snapshot of the blackboard state passed to detectors.
///     Contains all signals from prior detectors.
///     CRITICAL PII HANDLING RULES:
///     - PII (IP, UA, location, etc.) is accessed ONLY via direct properties (ClientIp, UserAgent, etc.)
///     - PII must NEVER be placed in signal payloads
///     - Signals contain ONLY boolean indicators (ip.available) or hashed values (ip.detected)
///     - Raw PII exists in memory only as long as detectors need it
/// </summary>
public sealed class BlackboardState
{
    /// <summary>
    ///     The HTTP context being analyzed
    /// </summary>
    public required HttpContext HttpContext { get; init; }

    /// <summary>
    ///     All signals collected so far.
    ///     IMPORTANT: Signals must NEVER contain raw PII.
    ///     Use PiiSignalHelper to emit privacy-safe signals.
    /// </summary>
    public required IReadOnlyDictionary<string, object> Signals { get; init; }

    /// <summary>
    ///     Current aggregated risk score (0.0 to 1.0)
    /// </summary>
    public double CurrentRiskScore { get; init; }

    /// <summary>
    ///     Which detectors have already run
    /// </summary>
    public required IReadOnlySet<string> CompletedDetectors { get; init; }

    /// <summary>
    ///     Which detectors failed
    /// </summary>
    public required IReadOnlySet<string> FailedDetectors { get; init; }

    /// <summary>
    ///     Contributions received so far
    /// </summary>
    public required IReadOnlyList<DetectionContribution> Contributions { get; init; }

    /// <summary>
    ///     Request ID for correlation
    /// </summary>
    public required string RequestId { get; init; }

    /// <summary>
    ///     Time elapsed since detection started
    /// </summary>
    public TimeSpan Elapsed { get; init; }

    // ===== PII Properties (Direct Access ONLY) =====
    //
    // CRITICAL: These properties provide direct access to PII.
    // Detectors needing raw PII MUST access it via these properties, NEVER from signals.
    // After detection completes, PII is cleared from memory.

    /// <summary>
    ///     Get the raw user agent (PII).
    ///     IMPORTANT: Access directly from state, NEVER put in signal payload.
    /// </summary>
    public string UserAgent => HttpContext.Request.Headers.UserAgent.ToString();

    /// <summary>
    ///     Get the client IP address (PII).
    ///     IMPORTANT: Access directly from state, NEVER put in signal payload.
    /// </summary>
    public string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    /// <summary>
    ///     Get the request path.
    /// </summary>
    public string Path => HttpContext.Request.Path.Value ?? "/";

    /// <summary>
    ///     Get the referer header (PII).
    ///     IMPORTANT: Access directly from state, NEVER put in signal payload.
    /// </summary>
    public string? Referer => HttpContext.Request.Headers.Referer.ToString();

    /// <summary>
    ///     Get the Accept-Language header (can be fingerprinting data).
    ///     IMPORTANT: Access directly from state, NEVER put in signal payload.
    /// </summary>
    public string? AcceptLanguage => HttpContext.Request.Headers.AcceptLanguage.ToString();

    /// <summary>
    ///     Get session ID if available (PII).
    ///     IMPORTANT: Access directly from state, NEVER put in signal payload.
    /// </summary>
    public string? SessionId => HttpContext.TraceIdentifier;

    /// <summary>
    ///     Get a typed signal value.
    ///     IMPORTANT: Signals should NEVER contain raw PII.
    /// </summary>
    public T? GetSignal<T>(string key)
    {
        return Signals.TryGetValue(key, out var value) && value is T typed ? typed : default;
    }

    /// <summary>
    ///     Check if a signal exists
    /// </summary>
    public bool HasSignal(string key)
    {
        return Signals.ContainsKey(key);
    }
}

/// <summary>
///     Base class for contributing detectors with common functionality
/// </summary>
public abstract class ContributingDetectorBase : IContributingDetector
{
    public abstract string Name { get; }

    public virtual int Priority => 100;
    public virtual bool IsEnabled => true;
    public virtual IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();
    public virtual TimeSpan TriggerTimeout => TimeSpan.FromMilliseconds(500);
    public virtual TimeSpan ExecutionTimeout => TimeSpan.FromSeconds(2);
    public virtual bool IsOptional => true;

    public abstract Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Helper to return a single contribution
    /// </summary>
    protected static IReadOnlyList<DetectionContribution> Single(DetectionContribution contribution)
    {
        return new[] { contribution };
    }

    /// <summary>
    ///     Helper to return multiple contributions
    /// </summary>
    protected static IReadOnlyList<DetectionContribution> Multiple(params DetectionContribution[] contributions)
    {
        return contributions;
    }

    /// <summary>
    ///     Helper to return no contributions
    /// </summary>
    protected static IReadOnlyList<DetectionContribution> None()
    {
        return Array.Empty<DetectionContribution>();
    }
}