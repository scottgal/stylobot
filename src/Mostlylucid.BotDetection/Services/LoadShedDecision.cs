using Mostlylucid.BotDetection.Policies;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Minimal abstraction over <see cref="PipelineLoadSensor.CurrentBand"/> so the
///     shed decision can be unit-tested without spinning up the real sensor.
/// </summary>
public interface ILoadBandSource
{
    LoadBand CurrentBand { get; }
}

/// <summary>
///     A cheap hint about whether the current request looks human or bot,
///     supplied by the caller from the per-signature verdict cache. Lets the
///     shed decision protect known humans under High load instead of shedding
///     them uniformly. Cold-cache (no prior verdict for this signature) is
///     <see cref="Unknown"/> -- random shed applies.
/// </summary>
public enum ShedHint
{
    /// <summary>No prior verdict on file for this request signature.</summary>
    Unknown,
    /// <summary>Verdict cache says this signature was last classified human.</summary>
    LikelyHuman,
    /// <summary>Verdict cache says this signature was last classified bot.</summary>
    LikelyBot
}

/// <summary>
///     Decides whether to shed (skip detection on) the current request based on
///     <see cref="ILoadBandSource.CurrentBand"/> and the policy-level <see cref="LoadShedOptions"/>.
///     <para>
///     Adaptive behaviour:
///     <list type="bullet">
///       <item>Low / Normal: never shed (regardless of options).</item>
///       <item>High: cheap verdict-cache hint biases the choice. Known humans are
///         protected (never shed). Known bots are preferentially shed. Cold-cache
///         requests fall back to random shed at <c>DropFractionAtHigh</c>.</item>
///       <item>Critical: the lookup is itself too expensive to gate on, so it's
///         ignored -- pure random shed at <c>DropFractionAtCritical</c>. This
///         keeps the gateway responsive even when the verdict cache mutex is
///         contended.</item>
///     </list>
///     The hash bucketing is deterministic on the request seed so retries land
///     on the same outcome.
///     </para>
/// </summary>
public sealed class LoadShedDecision
{
    private readonly ILoadBandSource _source;

    public LoadShedDecision(ILoadBandSource source) => _source = source;

    /// <summary>
    ///     Returns true when the current request should be shed (skip detection).
    /// </summary>
    /// <param name="options">Per-policy drop fractions.</param>
    /// <param name="requestSeed">Stable hash seed (e.g. connection id) so identical
    ///     requests get identical shed outcomes.</param>
    /// <param name="hint">Optional verdict-cache hint. Default
    ///     <see cref="ShedHint.Unknown"/> -- equivalent to the pre-adaptive behaviour.</param>
    public bool ShouldShed(LoadShedOptions options, int requestSeed, ShedHint hint = ShedHint.Unknown)
    {
        var band = _source.CurrentBand;

        // Low / Normal: never shed.
        if (band == LoadBand.Low || band == LoadBand.Normal) return false;

        if (band == LoadBand.High)
        {
            // Protect known humans -- the whole point of adaptive shedding.
            if (hint == ShedHint.LikelyHuman) return false;
            // Known bots: pull them down preferentially. Doubling the fraction
            // (capped at 1.0) makes a 0.2 base shed ~40% of known bots, leaving
            // the remaining capacity for cold-cache traffic.
            if (hint == ShedHint.LikelyBot)
                return DeterministicBucket.ShouldFire(requestSeed, Math.Min(1.0, options.DropFractionAtHigh * 2));
            // Unknown: random at base fraction.
            return DeterministicBucket.ShouldFire(requestSeed, options.DropFractionAtHigh);
        }

        // Critical: the verdict lookup itself is too expensive at this pressure.
        // Drop blind at the configured fraction; the cohort that survives still
        // gets the full pipeline.
        return DeterministicBucket.ShouldFire(requestSeed, options.DropFractionAtCritical);
    }
}
