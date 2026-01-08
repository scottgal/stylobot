using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Policies;

/// <summary>
///     Evaluates policy transitions based on blackboard state.
///     Determines when to transition between policies or take actions.
/// </summary>
public interface IPolicyEvaluator
{
    /// <summary>
    ///     Evaluate transitions for a policy given the current blackboard state.
    ///     Returns the transition result (continue, switch policy, or take action).
    /// </summary>
    PolicyEvaluationResult Evaluate(DetectionPolicy policy, BlackboardState state);

    /// <summary>
    ///     Get the effective weight for a detector within a policy.
    ///     Applies policy-level weight overrides and global weights.
    /// </summary>
    double GetEffectiveWeight(DetectionPolicy policy, string detectorName);
}

/// <summary>
///     Result of evaluating a policy's transitions.
/// </summary>
public sealed record PolicyEvaluationResult
{
    /// <summary>Whether to continue with the current policy</summary>
    public bool ShouldContinue { get; init; } = true;

    /// <summary>Policy to transition to (if any)</summary>
    public string? NextPolicy { get; init; }

    /// <summary>Action to take (if not continuing or transitioning)</summary>
    public PolicyAction? Action { get; init; }

    /// <summary>
    ///     Name of the action policy to execute (if specified in transition).
    ///     Takes precedence over Action for determining response behavior.
    /// </summary>
    public string? ActionPolicyName { get; init; }

    /// <summary>The transition that triggered this result</summary>
    public PolicyTransition? TriggeredBy { get; init; }

    /// <summary>Reason for the result</summary>
    public string? Reason { get; init; }

    /// <summary>Continue with current policy</summary>
    public static PolicyEvaluationResult Continue()
    {
        return new PolicyEvaluationResult();
    }

    /// <summary>Transition to another policy</summary>
    public static PolicyEvaluationResult TransitionTo(string policyName, PolicyTransition triggeredBy)
    {
        return new PolicyEvaluationResult
        {
            ShouldContinue = false,
            NextPolicy = policyName,
            TriggeredBy = triggeredBy,
            Reason = $"Transition to {policyName}"
        };
    }

    /// <summary>Take an immediate action</summary>
    public static PolicyEvaluationResult TakeAction(PolicyAction action, PolicyTransition triggeredBy)
    {
        return new PolicyEvaluationResult
        {
            ShouldContinue = false,
            Action = action,
            TriggeredBy = triggeredBy,
            Reason = $"Action: {action}"
        };
    }

    /// <summary>Execute a named action policy</summary>
    public static PolicyEvaluationResult ExecuteActionPolicy(string actionPolicyName, PolicyTransition triggeredBy)
    {
        return new PolicyEvaluationResult
        {
            ShouldContinue = false,
            ActionPolicyName = actionPolicyName,
            TriggeredBy = triggeredBy,
            Reason = $"ActionPolicy: {actionPolicyName}"
        };
    }
}

/// <summary>
///     Default implementation of policy evaluator.
/// </summary>
public class PolicyEvaluator : IPolicyEvaluator
{
    private readonly Dictionary<string, double> _globalWeights;
    private readonly ILogger<PolicyEvaluator> _logger;

    public PolicyEvaluator(ILogger<PolicyEvaluator> logger)
    {
        _logger = logger;

        // Default global weights - sensible defaults for all detectors
        _globalWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["UserAgent"] = 1.0,
            ["Header"] = 1.0,
            ["Ip"] = 1.0,
            ["Behavioral"] = 1.2,
            ["Inconsistency"] = 1.5,
            ["ClientSide"] = 1.3,
            ["Heuristic"] = 2.0, // Meta-layer that consumes all evidence
            ["Llm"] = 2.5, // AI/LLM escalation
            ["IpReputation"] = 1.5
        };
    }

    public PolicyEvaluationResult Evaluate(DetectionPolicy policy, BlackboardState state)
    {
        // Honor detector-driven early exits before running policy transitions or thresholds.
        var earlyExit = GetEarlyExitContribution(state);
        if (earlyExit != null)
        {
            var parsedVerdict = Enum.TryParse<EarlyExitVerdict>(earlyExit.EarlyExitVerdict, true, out var verdict)
                ? verdict : (EarlyExitVerdict?)null;
            var earlyExitAction = parsedVerdict.HasValue ? MapEarlyExitVerdictToAction(parsedVerdict.Value) : null;
            if (earlyExitAction.HasValue)
            {
                _logger.LogDebug(
                    "Policy {PolicyName} honoring early exit verdict {Verdict} from detector {Detector}",
                    policy.Name,
                    earlyExit.EarlyExitVerdict,
                    earlyExit.DetectorName);

                return PolicyEvaluationResult.TakeAction(
                    earlyExitAction.Value,
                    new PolicyTransition
                    {
                        WhenSignal = $"early-exit:{earlyExit.EarlyExitVerdict}",
                        Action = earlyExitAction.Value,
                        Description = $"Early exit verdict {earlyExit.EarlyExitVerdict}"
                    });
            }
        }

        foreach (var transition in policy.Transitions)
            if (ShouldTransition(transition, state))
            {
                _logger.LogDebug(
                    "Policy {PolicyName} triggered transition: {Description}",
                    policy.Name,
                    transition.Description ?? GetTransitionDescription(transition));

                // ActionPolicyName takes precedence over Action
                if (!string.IsNullOrEmpty(transition.ActionPolicyName))
                    return PolicyEvaluationResult.ExecuteActionPolicy(transition.ActionPolicyName, transition);

                if (transition.Action.HasValue)
                    return PolicyEvaluationResult.TakeAction(transition.Action.Value, transition);

                if (!string.IsNullOrEmpty(transition.GoToPolicy))
                    return PolicyEvaluationResult.TransitionTo(transition.GoToPolicy, transition);
            }

        // Check built-in thresholds
        if (state.CurrentRiskScore >= policy.ImmediateBlockThreshold)
        {
            _logger.LogDebug(
                "Policy {PolicyName} immediate block threshold ({Threshold}) exceeded with score {Score}",
                policy.Name, policy.ImmediateBlockThreshold, state.CurrentRiskScore);

            return PolicyEvaluationResult.TakeAction(
                PolicyAction.Block,
                new PolicyTransition
                {
                    WhenRiskExceeds = policy.ImmediateBlockThreshold,
                    Action = PolicyAction.Block,
                    Description = "Immediate block threshold exceeded"
                });
        }

        // Check if we should escalate to AI BEFORE early exit
        // This ensures AI detectors run when configured, even for low-risk scores
        // Skip if BypassTriggerConditions is true (all detectors including AI already ran)
        var aiAlreadyRan = policy.BypassTriggerConditions ||
                           (policy.AiPathDetectors.Count > 0 &&
                            state.CompletedDetectors.Any(d =>
                                policy.AiPathDetectors.Contains(d, StringComparer.OrdinalIgnoreCase)));

        if (policy.EscalateToAi &&
            state.CurrentRiskScore >= policy.AiEscalationThreshold &&
            !aiAlreadyRan)
        {
            _logger.LogDebug(
                "Policy {PolicyName} escalating to AI at score {Score} (threshold: {Threshold})",
                policy.Name, state.CurrentRiskScore, policy.AiEscalationThreshold);

            return PolicyEvaluationResult.TakeAction(
                PolicyAction.EscalateToAi,
                new PolicyTransition
                {
                    WhenRiskExceeds = policy.AiEscalationThreshold,
                    Action = PolicyAction.EscalateToAi,
                    Description = "Escalate to AI analysis"
                });
        }

        // Early exit check - only applies AFTER AI has run (or if AI is not configured)
        if (state.CurrentRiskScore <= policy.EarlyExitThreshold && policy.UseFastPath)
        {
            _logger.LogDebug(
                "Policy {PolicyName} early exit threshold ({Threshold}) met with score {Score}",
                policy.Name, policy.EarlyExitThreshold, state.CurrentRiskScore);

            return PolicyEvaluationResult.TakeAction(
                PolicyAction.Allow,
                new PolicyTransition
                {
                    WhenRiskBelow = policy.EarlyExitThreshold,
                    Action = PolicyAction.Allow,
                    Description = "Early exit threshold met"
                });
        }

        return PolicyEvaluationResult.Continue();
    }

    public double GetEffectiveWeight(DetectionPolicy policy, string detectorName)
    {
        // Check policy-level override first
        if (policy.WeightOverrides.TryGetValue(detectorName, out var policyWeight)) return policyWeight;

        // Fall back to global weight
        if (_globalWeights.TryGetValue(detectorName, out var globalWeight)) return globalWeight;

        // Default weight
        return 1.0;
    }

    private static DetectionContribution? GetEarlyExitContribution(BlackboardState state)
    {
        return state.Contributions.FirstOrDefault(c => c.TriggerEarlyExit && !string.IsNullOrEmpty(c.EarlyExitVerdict));
    }

    private static PolicyAction? MapEarlyExitVerdictToAction(EarlyExitVerdict verdict)
    {
        return verdict switch
        {
            EarlyExitVerdict.VerifiedGoodBot or
                EarlyExitVerdict.Whitelisted or
                EarlyExitVerdict.PolicyAllowed => PolicyAction.Allow,

            EarlyExitVerdict.VerifiedBadBot or
                EarlyExitVerdict.Blacklisted or
                EarlyExitVerdict.PolicyBlocked => PolicyAction.Block,

            _ => null
        };
    }

    private bool ShouldTransition(PolicyTransition transition, BlackboardState state)
    {
        // Check risk threshold conditions
        if (transition.WhenRiskExceeds.HasValue &&
            state.CurrentRiskScore < transition.WhenRiskExceeds.Value)
            return false;

        if (transition.WhenRiskBelow.HasValue &&
            state.CurrentRiskScore > transition.WhenRiskBelow.Value)
            return false;

        // Check signal conditions
        if (!string.IsNullOrEmpty(transition.WhenSignal))
        {
            if (!state.Signals.ContainsKey(transition.WhenSignal)) return false;

            // Check signal value if specified
            if (transition.WhenSignalValue != null)
                if (!state.Signals.TryGetValue(transition.WhenSignal, out var signalValue) ||
                    !Equals(signalValue, transition.WhenSignalValue))
                    return false;
        }

        // Check reputation state
        if (!string.IsNullOrEmpty(transition.WhenReputationState))
            if (!state.Signals.TryGetValue("ReputationState", out var repState) ||
                !string.Equals(repState?.ToString(), transition.WhenReputationState,
                    StringComparison.OrdinalIgnoreCase))
                return false;

        return true;
    }

    private static string GetTransitionDescription(PolicyTransition transition)
    {
        var parts = new List<string>();

        if (transition.WhenRiskExceeds.HasValue)
            parts.Add($"risk >= {transition.WhenRiskExceeds.Value:F2}");
        if (transition.WhenRiskBelow.HasValue)
            parts.Add($"risk <= {transition.WhenRiskBelow.Value:F2}");
        if (!string.IsNullOrEmpty(transition.WhenSignal))
            parts.Add($"signal '{transition.WhenSignal}'");
        if (!string.IsNullOrEmpty(transition.WhenReputationState))
            parts.Add($"reputation '{transition.WhenReputationState}'");

        var conditions = parts.Count > 0 ? string.Join(" AND ", parts) : "always";
        var action = transition.Action?.ToString() ?? transition.GoToPolicy ?? "unknown";

        return $"When {conditions} → {action}";
    }
}