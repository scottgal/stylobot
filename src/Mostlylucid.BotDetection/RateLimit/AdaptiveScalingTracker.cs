using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.RateLimit;

/// <summary>
///     Combines <see cref="DegradationAtom"/>'s rolling EMA signals with the
///     configured <see cref="AdaptiveScalingOptions.Tiers"/> ladder to
///     produce the current bot-rate-limit multiplier.
/// </summary>
public interface IAdaptiveScalingTracker
{
    /// <summary>
    ///     Multiplier applied to a <see cref="Actions.RateLimitActionPolicy"/>'s
    ///     effective <c>RequestsPerMinute</c>. <c>1.0</c> when adaptive
    ///     scaling is disabled or the origin is healthy.
    /// </summary>
    double CurrentMultiplier { get; }

    /// <summary>
    ///     Name of the active tier (e.g. <c>"nominal"</c> / <c>"degraded"</c>),
    ///     or <c>null</c> when adaptive scaling is disabled.
    /// </summary>
    string? CurrentTier { get; }

    /// <summary>When the current tier was entered. <c>null</c> when disabled.</summary>
    DateTime? TierEnteredAtUtc { get; }
}

/// <summary>
///     Default <see cref="IAdaptiveScalingTracker"/>. Reads
///     <see cref="DegradationAtom"/> on demand and gates tier entries
///     through <see cref="HysteresisTracker"/>.
/// </summary>
public sealed class AdaptiveScalingTracker : IAdaptiveScalingTracker
{
    private readonly DegradationAtom _degradation;
    private readonly HysteresisTracker _hysteresis;
    private readonly IOptions<AdaptiveScalingOptions> _options;
    private readonly ILogger<AdaptiveScalingTracker>? _logger;

    private string? _currentTier;
    private DateTime? _tierEnteredAtUtc;
    private double _currentMultiplier = 1.0;
    private readonly Lock _stateLock = new();

    public AdaptiveScalingTracker(
        DegradationAtom degradation,
        HysteresisTracker hysteresis,
        IOptions<AdaptiveScalingOptions> options,
        ILogger<AdaptiveScalingTracker>? logger = null)
    {
        _degradation = degradation;
        _hysteresis = hysteresis;
        _options = options;
        _logger = logger;
    }

    public double CurrentMultiplier
    {
        get
        {
            EvaluateTier();
            return _currentMultiplier;
        }
    }

    public string? CurrentTier
    {
        get
        {
            EvaluateTier();
            return _currentTier;
        }
    }

    public DateTime? TierEnteredAtUtc => _tierEnteredAtUtc;

    private void EvaluateTier()
    {
        var opts = _options.Value;
        if (!opts.Enabled || opts.Tiers.Count == 0)
        {
            lock (_stateLock)
            {
                _currentTier = null;
                _tierEnteredAtUtc = null;
                _currentMultiplier = 1.0;
            }
            return;
        }

        var p95 = _degradation.GetSignalValue(DegradationAtom.LatencyEmaMs);
        var err5xxRate = _degradation.GetSignalValue(DegradationAtom.Error5xxRate) * 100.0;

        // Tiers evaluated worst-first: find the highest-severity tier whose
        // threshold is exceeded *and* whose dwell time has elapsed. This is
        // the "trigger on N seconds of pain, recover slowly" gate.
        AdaptiveScalingOptions.Tier? activeTier = null;
        for (var i = opts.Tiers.Count - 1; i >= 0; i--)
        {
            var tier = opts.Tiers[i];
            var thresholdExceeded =
                (tier.P95LatencyMs > 0 && p95 >= tier.P95LatencyMs) ||
                (tier.Error5xxRatePct > 0 && err5xxRate >= tier.Error5xxRatePct);

            if (!thresholdExceeded) continue;

            // Hysteresis: require continuous condition for DwellSeconds before
            // *entering* this tier. Already-active tiers don't re-gate.
            if (_currentTier == tier.Name
                || _hysteresis.IsSatisfied($"tier:{tier.Name}", true, opts.Hysteresis.DwellSeconds))
            {
                activeTier = tier;
                break;
            }
        }

        // No tier active -> back to nominal (1.0). Recovery applies the
        // RecoveryMultiplier so a flap doesn't snap straight back to baseline.
        //
        // Caveat: recovery is *call-frequency-dependent*, not time-based --
        // each EvaluateTier call when health has recovered steps the
        // multiplier up by 1/RecoveryMultiplier. Busy servers recover in
        // milliseconds; quiet servers take minutes. Acceptable for now
        // because rate limits only matter when there is traffic; revisit
        // with a wall-clock recovery curve if production patterns expose
        // the asymmetry.
        if (activeTier is null)
        {
            lock (_stateLock)
            {
                if (_currentTier is not null)
                {
                    var recovered = Math.Min(1.0, _currentMultiplier / opts.Hysteresis.RecoveryMultiplier);
                    _logger?.LogInformation(
                        "AdaptiveScaling: recovering from tier {Tier} (multiplier {From:F2} -> {To:F2})",
                        _currentTier, _currentMultiplier, recovered);
                    _currentMultiplier = recovered;
                    if (Math.Abs(_currentMultiplier - 1.0) < 0.01)
                    {
                        _currentMultiplier = 1.0;
                        _currentTier = null;
                        _tierEnteredAtUtc = null;
                    }
                }
                else
                {
                    _currentMultiplier = 1.0;
                }
            }
            return;
        }

        lock (_stateLock)
        {
            if (_currentTier != activeTier.Name)
            {
                _logger?.LogInformation(
                    "AdaptiveScaling: entering tier {Tier} (p95={P95:F0}ms, 5xx={Err:F1}%, multiplier {Mult:F2})",
                    activeTier.Name, p95, err5xxRate, activeTier.BotMultiplier);
                _currentTier = activeTier.Name;
                _tierEnteredAtUtc = DateTime.UtcNow;
            }
            _currentMultiplier = activeTier.BotMultiplier;
        }
    }
}
