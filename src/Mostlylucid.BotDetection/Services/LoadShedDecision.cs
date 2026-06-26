using Mostlylucid.BotDetection.Policies;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Minimal abstraction over <see cref="PipelineLoadSensor.CurrentBand"/>
///     so the shed decision can be unit-tested without spinning up the real
///     sensor.
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
///     <see cref="Unknown"/> - random shed applies.
/// </summary>
[Obsolete("Replaced by VisitorClass in Task 7")]
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
///     Visitor-class-aware shed decision. Resolves the per-band, per-class
///     shed fraction from <see cref="LoadShedOptions"/> and rolls a
///     deterministic bucket from the request seed.
///     <para>
///     Contract: humans never shed by default (operator must explicitly set
///     <see cref="LoadShedOptions.HumanShedAtCritical"/> &gt; 0 to opt in);
///     bots always shed when the band escalates; unknowns shed at the
///     configured fraction. Low and Normal bands always pass regardless of
///     class.
///     </para>
/// </summary>
public sealed class LoadShedDecision
{
    private readonly ILoadBandSource _source;

    public LoadShedDecision(ILoadBandSource source) => _source = source;

    /// <summary>
    ///     Returns true when the current request should be shed (refused
    ///     with 503 + Retry-After when band is Critical; skip detection +
    ///     forward when band is High).
    /// </summary>
    /// <param name="visitorClass">
    ///     Resolved by <see cref="ClassGateResolver.Resolve"/> from the
    ///     cached fingerprint verdict against the policy's
    ///     <see cref="LoadShedOptions.HumanGate"/> /
    ///     <see cref="LoadShedOptions.BotGate"/>.
    /// </param>
    /// <param name="options">Per-policy shed fractions.</param>
    /// <param name="requestSeed">
    ///     Stable hash seed (e.g. connection id) so identical requests get
    ///     identical shed outcomes.
    /// </param>
    public bool ShouldShed(VisitorClass visitorClass, LoadShedOptions options, int requestSeed)
    {
        var band = _source.CurrentBand;
        if (band == LoadBand.Low || band == LoadBand.Normal) return false;

        var fraction = (visitorClass, band) switch
        {
            (VisitorClass.Human,   LoadBand.High)     => options.HumanShedAtHigh,
            (VisitorClass.Human,   LoadBand.Critical) => options.HumanShedAtCritical,
            (VisitorClass.Unknown, LoadBand.High)     => options.UnknownShedAtHigh,
            (VisitorClass.Unknown, LoadBand.Critical) => options.UnknownShedAtCritical,
            (VisitorClass.Bot,     LoadBand.High)     => options.BotShedAtHigh,
            (VisitorClass.Bot,     LoadBand.Critical) => options.BotShedAtCritical,
            _ => 0.0,
        };
        return DeterministicBucket.ShouldFire(requestSeed, fraction);
    }

    /// <summary>
    ///     Legacy overload for backward compatibility with Task 8 middleware.
    ///     Maps ShedHint to VisitorClass before calling the new ShouldShed.
    ///     Task 8 will remove this and update all call sites directly.
    /// </summary>
    [Obsolete("Use ShouldShed(VisitorClass, LoadShedOptions, int) instead. Task 8 will remove this.")]
    public bool ShouldShed(LoadShedOptions options, int requestSeed, ShedHint hint = ShedHint.Unknown)
    {
        var visitorClass = hint switch
        {
            ShedHint.LikelyHuman => VisitorClass.Human,
            ShedHint.LikelyBot => VisitorClass.Bot,
            _ => VisitorClass.Unknown,
        };
        return ShouldShed(visitorClass, options, requestSeed);
    }
}
