namespace Mostlylucid.BotDetection.Policies;

/// <summary>
///     Per-policy thresholds for the SignatureVerdictGate. Four behaviours:
///     <list type="bullet">
///         <item>
///             <b>Skip</b>: live signature state meets <see cref="SkipMinConfidence"/> AND was
///             observed within <see cref="SkipMaxAgeSeconds"/>. The watchdog confirms nothing
///             variant. Bypass the detector pipeline and enforce the cached verdict.
///         </item>
///         <item>
///             <b>Watchdog-trip</b>: Skip candidate but VarianceWatchdog detected an unusual
///             signal. Cache invalidated; full pipeline runs.
///         </item>
///         <item>
///             <b>Bias</b>: live signature state meets <see cref="BiasMinConfidence"/> but does
///             not qualify for Skip. Run the pipeline AND inject the verdict as a prior.
///         </item>
///         <item>
///             <b>Miss</b>: no usable state (cold fingerprint, below BiasMinConfidence, or
///             too stale). Run the full pipeline with no prior.
///         </item>
///     </list>
///     Confidence is direction-agnostic: a sure-bot AND a sure-human both qualify for Skip
///     when their confidence is high enough.
/// </summary>
public sealed record SignatureCacheOptions
{
    /// <summary>Minimum confidence required to skip the pipeline entirely. Default 0.85.</summary>
    public double SkipMinConfidence { get; init; } = 0.85;

    /// <summary>Maximum age in seconds for a Skip-eligible verdict. Default 300 (5 minutes).</summary>
    public int SkipMaxAgeSeconds { get; init; } = 300;

    /// <summary>Minimum confidence required to inject a prior bias. Default 0.30.</summary>
    public double BiasMinConfidence { get; init; } = 0.30;

    /// <summary>Maximum age in seconds for a Bias-eligible verdict. Default 86400 (24h).</summary>
    public int BiasMaxAgeSeconds { get; init; } = 86_400;

    /// <summary>
    ///     Fraction of Skip-eligible requests that nevertheless run the pipeline so the
    ///     verdict cache stays honest. Default 0.05 (5 percent). Set to 0 to disable
    ///     refresh sampling.
    /// </summary>
    public double SkipSamplingRate { get; init; } = 0.05;

    /// <summary>
    ///     Whether the gate is enabled at all on this policy. Default true. Set to false
    ///     to disable cache-aware behaviour and always run the pipeline (high-security
    ///     endpoints, debug builds, etc.).
    /// </summary>
    public bool Enabled { get; init; } = true;
}
