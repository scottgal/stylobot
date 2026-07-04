using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     Foundation SensorAtom (per Taxonomy.md) that injects the cached fingerprint
///     verdict as a prior bias. Effective weight = prior_confidence * multiplier
///     * linear-age-decay, so stale priors lose all weight. ConfidenceDelta maps
///     probability to [-1, +1].
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>FingerprintPriorContributor</c>. Reads
///         <see cref="SignalKeys.FingerprintPriorProbability"/> /
///         <see cref="SignalKeys.FingerprintPriorConfidence"/> /
///         <see cref="SignalKeys.FingerprintPriorAgeSeconds"/> off
///         <see cref="HttpContext.Items"/> where the middleware stashed them
///         at gate-time.
///     </para>
///     <para>
///         Reading Items directly is the correct shape for this boundary atom
///         until a dedicated FingerprintPriorHydratorAtom raises the values as
///         sink signals (Model 2 hints). Priority 4 matches the legacy
///         contributor's Wave-0 slot.
///     </para>
///     <para>
///         Config-driven thresholds resolved via
///         <see cref="IDetectorConfigProvider"/> against the manifest name
///         "FingerprintPrior" (same key the legacy contributor used, so
///         appsettings overrides on <c>BotDetection:Detectors:FingerprintPrior:*</c>
///         apply to this atom too).
///     </para>
/// </remarks>
public sealed class FingerprintPriorAtom : DetectorAtomBase
{
    public const string DetectorName = "FingerprintPrior";

    private readonly ILogger<FingerprintPriorAtom> _logger;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public FingerprintPriorAtom(
        ILogger<FingerprintPriorAtom> logger,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor)
        : base(name: DetectorName, category: DetectorName)
    {
        _logger = logger;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
    }

    public override int Priority => 4;

    private double WeightMultiplier =>
        _configProvider.GetParameter(DetectorName, "prior_weight_multiplier", 1.0);

    private double AgeDecayHorizon =>
        _configProvider.GetParameter(DetectorName, "age_decay_horizon_seconds", 86_400.0);

    private bool DetailedLogging =>
        _configProvider.GetParameter(DetectorName, "detailed_logging", false);

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null)
            return Task.FromResult(None());

        if (!TryReadDouble(context, SignalKeys.FingerprintPriorProbability, out var prob) ||
            !TryReadDouble(context, SignalKeys.FingerprintPriorConfidence, out var conf))
        {
            return Task.FromResult(None());
        }

        var age = TryReadDouble(context, SignalKeys.FingerprintPriorAgeSeconds, out var a) ? a : 0.0;

        var horizon = AgeDecayHorizon;
        var decay = horizon > 0.0 ? Math.Max(0.0, 1.0 - age / horizon) : 1.0;
        var effectiveWeight = conf * WeightMultiplier * decay;

        if (effectiveWeight <= 0.0)
            return Task.FromResult(None());

        var delta = 2.0 * (prob - 0.5);

        if (DetailedLogging)
        {
            _logger.LogDebug(
                "FingerprintPrior contribution: delta={Delta:F3}, weight={Weight:F3}, prob={Prob:F2}, conf={Conf:F2}, age={Age:F0}s",
                delta, effectiveWeight, prob, conf, age);
        }

        return Task.FromResult(Single(new DetectionContribution
        {
            DetectorName = DetectorName,
            Category = DetectorName,
            ConfidenceDelta = delta,
            Weight = effectiveWeight,
            Reason = $"Cached fingerprint verdict (prob={prob:F2}, conf={conf:F2}, age={age:F0}s)"
        }));
    }

    private static bool TryReadDouble(HttpContext context, string key, out double value)
    {
        if (context.Items.TryGetValue(key, out var item) && TryToDouble(item, out value))
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