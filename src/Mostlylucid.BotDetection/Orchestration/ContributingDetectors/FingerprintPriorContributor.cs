using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Wave 0 contributor that injects the cached fingerprint verdict as a prior bias.
///     Reads fingerprint.prior.* values written by the middleware (after the
///     SignatureVerdictGate returns a Bias decision) and emits a single contribution
///     so the orchestrator's existing weighted-sum aggregation pulls the per-request
///     posterior toward the prior.
///
///     Effective contribution weight = prior_confidence * multiplier * linear-age-decay,
///     so old priors lose all weight and very-recent confident priors strongly anchor
///     the posterior. ConfidenceDelta maps prior probability to [-1, +1]:
///     prob = 0.0 -> -1.0 (strong human), prob = 0.5 -> 0.0 (neutral),
///     prob = 1.0 -> +1.0 (strong bot).
///
///     Reads from state.Signals first (test path), then falls back to
///     state.HttpContext.Items (production path, where the middleware stashes
///     the prior values before the orchestrator runs).
/// </summary>
public class FingerprintPriorContributor : ConfiguredContributorBase
{
    private readonly ILogger<FingerprintPriorContributor> _logger;

    public FingerprintPriorContributor(
        ILogger<FingerprintPriorContributor> logger,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
    }

    public override string Name => "FingerprintPrior";
    public override int Priority => Manifest?.Priority ?? 4;
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    private double WeightMultiplier => GetParam("prior_weight_multiplier", 1.0);
    private double AgeDecayHorizon => GetParam("age_decay_horizon_seconds", 86_400.0);

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state, CancellationToken cancellationToken = default)
    {
        if (!TryReadDouble(state, SignalKeys.FingerprintPriorProbability, out var prob) ||
            !TryReadDouble(state, SignalKeys.FingerprintPriorConfidence, out var conf))
        {
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(Array.Empty<DetectionContribution>());
        }

        var age = TryReadDouble(state, SignalKeys.FingerprintPriorAgeSeconds, out var a) ? a : 0.0;

        var horizon = AgeDecayHorizon;
        var decay = horizon > 0.0 ? Math.Max(0.0, 1.0 - age / horizon) : 1.0;
        var effectiveWeight = conf * WeightMultiplier * decay;

        if (effectiveWeight <= 0.0)
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(Array.Empty<DetectionContribution>());

        // Map prior probability to confidence delta in [-1, +1].
        var delta = 2.0 * (prob - 0.5);

        // Construct the contribution directly so weight is driven by the
        // age-decayed prior confidence rather than the YAML-configured constants.
        var contribution = new DetectionContribution
        {
            DetectorName = Name,
            Category = "FingerprintPrior",
            ConfidenceDelta = delta,
            Weight = effectiveWeight,
            Reason = $"Cached fingerprint verdict (prob={prob:F2}, conf={conf:F2}, age={age:F0}s)"
        };

        if (DetailedLogging)
        {
            _logger.LogDebug(
                "FingerprintPrior contribution: delta={Delta:F3}, weight={Weight:F3}, prob={Prob:F2}, conf={Conf:F2}, age={Age:F0}s",
                delta, effectiveWeight, prob, conf, age);
        }

        return Task.FromResult<IReadOnlyList<DetectionContribution>>(new[] { contribution });
    }

    /// <summary>
    ///     Tries to read a numeric prior value from either the blackboard signals
    ///     or HttpContext.Items. Handles boxed doubles, ints, and any IConvertible
    ///     numeric type.
    /// </summary>
    private static bool TryReadDouble(BlackboardState state, string key, out double value)
    {
        if (state.Signals.TryGetValue(key, out var sig) && TryToDouble(sig, out value))
            return true;

        var items = state.HttpContext?.Items;
        if (items is not null && items.TryGetValue(key, out var item) && TryToDouble(item, out value))
            return true;

        value = 0.0;
        return false;
    }

    private static bool TryToDouble(object? raw, out double value)
    {
        switch (raw)
        {
            case null:
                value = 0.0;
                return false;
            case double d:
                value = d;
                return true;
            case float f:
                value = f;
                return true;
            case int i:
                value = i;
                return true;
            case long l:
                value = l;
                return true;
            case IConvertible conv:
                try
                {
                    value = conv.ToDouble(System.Globalization.CultureInfo.InvariantCulture);
                    return true;
                }
                catch
                {
                    value = 0.0;
                    return false;
                }
            default:
                value = 0.0;
                return false;
        }
    }
}
