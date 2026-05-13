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
///     Decides whether to shed (skip detection on) the current request based on
///     <see cref="ILoadBandSource.CurrentBand"/> and the policy-level <see cref="LoadShedOptions"/>.
///     Deterministic: a stable hash of the requestSeed decides whether the request falls
///     in the shed bucket, so identical seeds produce identical results. The middleware
///     uses the request connection id as the seed.
/// </summary>
public sealed class LoadShedDecision
{
    private readonly ILoadBandSource _source;

    public LoadShedDecision(ILoadBandSource source) => _source = source;

    /// <summary>
    ///     Returns true when the current request should be shed (skip detection).
    ///     Always false when the load band is Low or Normal, regardless of options.
    /// </summary>
    public bool ShouldShed(LoadShedOptions options, int requestSeed)
    {
        var fraction = _source.CurrentBand switch
        {
            LoadBand.High     => options.DropFractionAtHigh,
            LoadBand.Critical => options.DropFractionAtCritical,
            _                 => 0.0,
        };
        return DeterministicBucket.ShouldFire(requestSeed, fraction);
    }
}
