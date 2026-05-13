using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Wave 0 contributor that injects the cached fingerprint verdict as a prior bias.
///     Effective weight = prior_confidence * multiplier * linear-age-decay, so stale
///     priors lose all weight. ConfidenceDelta maps probability to [-1, +1].
///
///     Reads from <c>state.Signals</c> first, then <c>HttpContext.Items</c>: the
///     middleware writes to Items before the orchestrator runs, but tests seed
///     signals directly.
/// </summary>
public class FingerprintPriorContributor : ConfiguredContributorBase
{
    public const string DetectorName = "FingerprintPrior";

    private static readonly Task<IReadOnlyList<DetectionContribution>> _emptyResult =
        Task.FromResult<IReadOnlyList<DetectionContribution>>(Array.Empty<DetectionContribution>());

    private readonly ILogger<FingerprintPriorContributor> _logger;

    public FingerprintPriorContributor(
        ILogger<FingerprintPriorContributor> logger,
        IDetectorConfigProvider configProvider)
        : base(configProvider)
    {
        _logger = logger;
    }

    public override string Name => DetectorName;
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
            return _emptyResult;
        }

        var age = TryReadDouble(state, SignalKeys.FingerprintPriorAgeSeconds, out var a) ? a : 0.0;

        var horizon = AgeDecayHorizon;
        var decay = horizon > 0.0 ? Math.Max(0.0, 1.0 - age / horizon) : 1.0;
        var effectiveWeight = conf * WeightMultiplier * decay;

        if (effectiveWeight <= 0.0)
            return _emptyResult;

        var delta = 2.0 * (prob - 0.5);

        var contribution = new DetectionContribution
        {
            DetectorName = DetectorName,
            Category = DetectorName,
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
